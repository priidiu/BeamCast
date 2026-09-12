using System.Collections.ObjectModel;
using System.IO;
using AirNext.Core.Discovery;

namespace AirNext_App;

/// <summary>
/// mDNS device discovery (RHI-169) — wrapper around Core MdnsBrowser.
/// - live devices (_airplay + _raop)
/// - compatible (RAOP, port 7000) sorted to the top
/// - remembers last-used device (auto-stream, decision 2c)
/// </summary>
public sealed class DeviceDiscoveryService : IDisposable
{
    private readonly MdnsBrowser _browser;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiDispatcher;

    /// <summary>Discovered devices (compatible first).</summary>
    public ObservableCollection<DeviceItem> Devices { get; } = new();

    /// <summary>Device count changed — UI status (RHI-169).</summary>
    public event Action<int>? DevicesCountChanged;

    /// <summary>Last-used device (from settings) — auto-stream.</summary>
    public AirplayDevice? LastUsedDevice { get; private set; }

    public long PacketsReceived => _browser.PacketsReceived;

    private readonly Dictionary<string, DeviceItem> _byFullName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public DeviceDiscoveryService(Microsoft.UI.Dispatching.DispatcherQueue uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
        _browser = new MdnsBrowser();
        _browser.DeviceAdded += OnDeviceAdded;
        LastUsedDevice = LoadLastUsedDevice();
    }

    public void Start()
    {
        try
        {
            // Aggressive re-query (0.75 s) — WiiM answers mDNS late (~60 s
            // with a 2 s interval). Shorter interval = more chances in ~10–15 s.
            _browser.Start(queryInterval: TimeSpan.FromMilliseconds(750));
            AppLog.Write($"Discovery: MdnsBrowser start; diag={string.Join("; ", _browser.Diagnostics)}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Discovery: MdnsBrowser start FAIL: {ex}");
        }

        // RHI-169: unicast query to last device IP — bypasses
        // WiiM/Linkplay multicast cooldown (~45 s).
        if (LastUsedDevice is { Host: var lastIp } && !string.IsNullOrEmpty(lastIp))
        {
            AppLog.Write($"Discovery: unicast to last device {lastIp}");
            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 120 && Devices.Count == 0; i++)
                    {
                        _browser.SendQueryUnicast(lastIp);
                        await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);
        }

        // Periodic re-sort (devices arrive over time)
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(3000, _cts.Token).ConfigureAwait(false);
                    // Diagnostics: is multicast receiving packets (log off the UI thread)
                    if (_browser.PacketsReceived > 0)
                        AppLog.Write($"Discovery: RX packets={_browser.PacketsReceived}, devices={Devices.Count}");
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private void OnDeviceAdded(AirplayDevice device)
    {
        // ObservableCollection must be mutated on the UI thread (WinUI binding)
        _ = _uiDispatcher.TryEnqueue(() => AddDevice(device));
    }

    private void AddDevice(AirplayDevice device)
    {
        var item = new DeviceItem
        {
            Name = device.Name,
            Ip = BuildSubtext(device),
            StatusText = IsCompatible(device) ? "● Available" : "Not RAOP",
            Status = IsCompatible(device) ? DeviceStatus.Active : DeviceStatus.Incompatible,
            Device = device,
        };

        lock (_lock)
        {
            if (!_byFullName.ContainsKey(device.FullName))
            {
                _byFullName[device.FullName] = item;
                Devices.Add(item);
            }
        }
        AppLog.Write($"Discovery: found {device.FullName} ({device.Host}:{device.Port})");
        DevicesCountChanged?.Invoke(Devices.Count);
        Reorder();
    }

    private static string BuildSubtext(AirplayDevice d)
    {
        string type = d.ServiceType == "raop" ? "AirPlay" : "AirPlay";
        return $"{d.Host} · {type} · port {d.Port}";
    }

    private void Reorder()
    {
        List<DeviceItem> sorted;
        lock (_lock)
        {
            sorted = Devices
                .OrderByDescending(d => IsCompatible(d.Device!))
                .ThenBy(d => d.Name)
                .ToList();
        }
        // ObservableCollection — move in place (no Clear, keeps binding)
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Devices.IndexOf(sorted[i]);
            if (cur != i)
                Devices.Move(cur, i);
        }
    }

    /// <summary>Kompatybilne = RAOP/AP1: port 7000 (_airplay) lub port 5000 (_raop).</summary>
    public static bool IsCompatible(AirplayDevice d) =>
        d.Port == 7000 || d.Port == 5000;

    /// <summary>Remember last-used device (auto-stream + persist).</summary>
    public void SetLastUsed(AirplayDevice device)
    {
        LastUsedDevice = device;
        SaveLastUsedDevice(device);
        AppLog.Write($"Discovery: last-used = {device.FullName}");
    }

    // --- Persistence (settings) ---
    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamCast", "last-device.json");

    private static AirplayDevice? LoadLastUsedDevice()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var d = System.Text.Json.JsonSerializer.Deserialize<AirplayDevice>(json);
                AppLog.Write("Discovery: last-device loaded = " + d?.FullName);
                return d;
            }
        }
        catch (Exception ex) { AppLog.Write($"Discovery: load last-device FAIL: {ex.Message}"); }
        return null;
    }

    private static void SaveLastUsedDevice(AirplayDevice device)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(device);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) { AppLog.Write($"Discovery: save last-device FAIL: {ex.Message}"); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _browser.Dispose();
    }
}
