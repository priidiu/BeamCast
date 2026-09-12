using System.Net;
using System.Runtime.InteropServices;

namespace AirNext.Core.Discovery;

/// <summary>
/// mDNS browser przez NATYWNE API Windows (RHI-160) — DnsServiceBrowse/Resolve (dnsapi.dll).
/// Solves instability own socketa multicast na Windows (konflikt z natywnym
/// resolverem o port 5353). Used gdy RuntimeInformation.IsOSPlatform(Windows).
/// </summary>
public sealed class NativeMdnsBrowser : IDisposable
{
    private readonly Dictionary<string, AirplayDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

 // Callbacki i QueryName must live (GC!) — trzymamy referencje
    private readonly List<DnsServiceNative.DnsServiceBrowseCallback> _browseCallbacks = new();
    private readonly List<DnsServiceNative.DnsServiceResolveCallback> _resolveCallbacks = new();
    private readonly List<IntPtr> _queryNameAllocs = new();
    private readonly List<DnsServiceNative.DnsServiceBrowseRequest> _browseRequests = new();
    private bool _disposed;

 // DnsServiceBrowseCancel przyjmuje ref do struktury — trzymamy array z elementami
    private DnsServiceNative.DnsServiceBrowseRequest[] _browseRequestArray = Array.Empty<DnsServiceNative.DnsServiceBrowseRequest>();

    public IReadOnlyList<AirplayDevice> Devices { get { lock (_lock) return _devices.Values.ToList(); } }
    public event Action<AirplayDevice>? DeviceAdded;
    public List<string> Diagnostics { get; } = new();

    public void Start(TimeSpan? _ = null)
    {
        Browse("_airplay._tcp.local");
        Browse("_raop._tcp.local");
    }

    private void Browse(string service)
    {
        DnsServiceNative.DnsServiceBrowseCallback cb = (status, ctx, instancePtr) =>
        {
            if (status == DnsServiceNative.DnsSuccess && instancePtr != 0)
            {
                var inst = DnsServiceNative.ReadInstance(instancePtr);
                if (inst is { } i && !string.IsNullOrEmpty(i.InstanceName))
                    Resolve(i.InstanceName);
            }
            else if (status != DnsServiceNative.DnsSuccess && status != DnsServiceNative.DnsRequestPending)
            {
                Diagnostics.Add($"browse {service} status=0x{status:X}");
            }
        };
        _browseCallbacks.Add(cb); // keep alive

        var queryName = DnsServiceNative.AllocString(service);
        _queryNameAllocs.Add(queryName);

        var request = new DnsServiceNative.DnsServiceBrowseRequest
        {
            Version = DnsServiceNative.DnsQueryRequestVersion1,
            InterfaceIndex = 0, // wszystkie interfejsy
            QueryName = queryName,
            BrowseCallback = DnsServiceNative.FnPtr(cb),
            BrowseContext = IntPtr.Zero,
        };

        uint hr = DnsServiceNative.DnsServiceBrowse(ref request);
        if (hr != DnsServiceNative.DnsSuccess)
            Diagnostics.Add($"DnsServiceBrowse({service}) FAIL hr=0x{hr:X}");
        else
        {
            _browseRequests.Add(request); // keep request (QueryName) alive
            _browseRequestArray = _browseRequests.ToArray();
        }
    }

    private void Resolve(string instanceName)
    {
        DnsServiceNative.DnsServiceResolveCallback cb = (status, ctx, instancePtr) =>
        {
            if (status == DnsServiceNative.DnsSuccess && instancePtr != 0)
            {
                var inst = DnsServiceNative.ReadInstance(instancePtr);
                if (inst is { } i && !string.IsNullOrEmpty(i.HostName))
                {
                    // TXT z pszText (null-separated "key=value\0key=value\0")
                    var txt = ParseTextRecords(i.Text);
                    ulong features = 0;
                    if (txt.TryGetValue("tp", out var tpHex) &&
                        ulong.TryParse(tpHex, System.Globalization.NumberStyles.HexNumber, null, out var tp))
                        features = tp;

                    var device = new AirplayDevice
                    {
                        Name = i.InstanceName.Split('.')[0],
                        Host = i.HostName,
                        Port = i.Port,
                        ServiceType = i.InstanceName.Contains("_raop._tcp", StringComparison.OrdinalIgnoreCase) ? "raop" : "airplay",
                        FullName = i.InstanceName,
                        Txt = txt,
                        FeaturesRaw = features,
                    };

                    lock (_lock)
                    {
                        if (!_devices.ContainsKey(device.FullName))
                        {
                            _devices[device.FullName] = device;
                            DeviceAdded?.Invoke(device);
                        }
                    }
                }
            }
        };
        _resolveCallbacks.Add(cb);

        var queryName = DnsServiceNative.AllocString(instanceName);
        _queryNameAllocs.Add(queryName);

        var request = new DnsServiceNative.DnsServiceResolveRequest
        {
            Version = DnsServiceNative.DnsQueryRequestVersion1,
            InterfaceIndex = 0,
            QueryName = queryName,
            ResolveCallback = DnsServiceNative.FnPtr(cb),
            ResolveContext = IntPtr.Zero,
        };

        uint hr = DnsServiceNative.DnsServiceResolve(ref request);
        if (hr != DnsServiceNative.DnsSuccess)
            Diagnostics.Add($"DnsServiceResolve({instanceName}) hr=0x{hr:X}");
    }

 /// <summary>Parsuje pszText (null-separated key=value) na dictionary.</summary>
    private static Dictionary<string, string> ParseTextRecords(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq >= 0)
                result[entry[..eq]] = entry[(eq + 1)..];
            else
                result[entry] = "";
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < _browseRequestArray.Length; i++)
        {
            try { DnsServiceNative.DnsServiceBrowseCancel(ref _browseRequestArray[i]); } catch { }
        }

        foreach (var p in _queryNameAllocs)
        {
            try { Marshal.FreeHGlobal(p); } catch { }
        }
    }
}
