using System.Collections.ObjectModel;
using AirNext.Core.Discovery;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AirNext_App;

/// <summary>
/// Main BeamCast window (RHI-168/169/170) — Fluent + system theme, layout from mockup.
/// RHI-169: device list from live mDNS.
/// RHI-170: transport controls (start/stop, pause, volume) + optional PC mute.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DeviceDiscoveryService _discovery;
    private readonly BeamService _beam;
    private readonly LatencyServer _latencyServer;
    private readonly AppSettings _settings;
    private AirNext.Core.Discovery.AirplayDevice? _selectedDevice;
    private bool _isStreaming;
    private bool _suppressSelectionStart;
    private bool _exitRequested;

    public MainWindow()
    {
        AppLog.Write("MainWindow ctor: start");
        InitializeComponent();
        Title = "BeamCast";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 560));

        _settings = AppSettings.Load();

        // BeamService before auto-select — SelectionChanged calls StartAsync
        _beam = new BeamService(DispatcherQueue);
        _beam.RealTimeMode = _settings.RealTimeMode;
        _beam.StatusChanged += OnBeamStatusChanged;
        _beam.MetricsUpdated += OnBeamMetricsUpdated;
        _beam.StreamingStarted += () => _isStreaming = true;

        RealTimeToggle.IsOn = _settings.RealTimeMode;
        _beam.RealTimeDelayMs = Math.Clamp(_settings.RealTimeDelayMs <= 0 ? 40 : _settings.RealTimeDelayMs, 20, 250);
        DelaySlider.Value = _beam.RealTimeDelayMs;
        DelayText.Text = _beam.RealTimeDelayMs + " ms";
        DelaySlider.IsEnabled = _settings.RealTimeMode;

        // Discovery (RHI-169)
        _discovery = new DeviceDiscoveryService(DispatcherQueue);
        _discovery.DevicesCountChanged += OnDevicesCountChanged;
        DevicesList.ItemsSource = _discovery.Devices;
        DevicesList.SelectionChanged += DevicesList_SelectionChanged;
        _discovery.Start();

        StartSearchStatusTimer();

        // LatencyServer (RHI-173)
        _latencyServer = new LatencyServer();
        _beam.MetricsUpdated += m => _latencyServer.UpdateMetrics(m);
        _beam.StreamingStopped += () =>
        {
            _isStreaming = false;
            _latencyServer.Clear();
        };
        try { _latencyServer.Start(); } catch (Exception ex) { AppLog.Write($"LatencyServer start FAIL: {ex.Message}"); }

        VolumeSlider.Value = 62;
        VolumeText.Text = "62";

        if (_settings.AutoStreamLastDevice)
            AutoSelectLastUsed();

        AppWindow.Closing += OnAppWindowClosing;
        AppLog.Write("MainWindow ctor: OK, discovery + beam started");
    }

    /// <summary>X hides to tray (stream keeps running). Tray Exit calls <see cref="ExitAsync"/>.</summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
            return;
        args.Cancel = true;
        AppWindow.Hide();
        AppLog.Write("MainWindow: close → tray");
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    public async Task ExitAsync()
    {
        _exitRequested = true;
        try { await _beam.StopAsync().ConfigureAwait(true); } catch (Exception ex) { AppLog.Write($"Exit: stop FAIL: {ex.Message}"); }
        try { _discovery.Dispose(); } catch { }
        try { await _latencyServer.DisposeAsync().ConfigureAwait(true); } catch { }
        AppLog.Write("MainWindow: exit");
        DispatcherQueue.TryEnqueue(() => Close());
    }

    private void RealTimeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_beam is null) return;
        _beam.RealTimeMode = RealTimeToggle.IsOn;
        _settings.RealTimeMode = RealTimeToggle.IsOn;
        DelaySlider.IsEnabled = RealTimeToggle.IsOn;
        _settings.Save();
        AppLog.Write($"RealTime mode: {(RealTimeToggle.IsOn ? "ON" : "OFF")} delay={_beam.RealTimeDelayMs}ms (WASAPI applies on next start)");
    }

    private void DelaySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_beam is null) return;
        int ms = (int)Math.Round(e.NewValue);
        ms = Math.Clamp(ms, 20, 250);
        DelayText.Text = ms + " ms";
        _beam.RealTimeDelayMs = ms;
        _settings.RealTimeDelayMs = ms;
        _settings.Save();
    }

    /// <summary>
    /// RHI-169 decision 2c: wait until last-used appears on mDNS (WiiM ~47–61 s)
    /// i od razu wystartuj stream — nie tylko zaznacz wiersz.
    /// </summary>
    private void AutoSelectLastUsed()
    {
        var last = _discovery.LastUsedDevice;
        if (last is null) return;
        AppLog.Write("Auto-stream: waiting for last-used = " + last.FullName + " (" + last.Host + ")");

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 90; i++)
            {
                DeviceItem? match = null;
                foreach (var d in _discovery.Devices)
                {
                    var dev = d.Device;
                    if (dev is null) continue;
                    if (string.Equals(dev.FullName, last.FullName, StringComparison.OrdinalIgnoreCase)
                        || (dev.Host == last.Host && dev.Port == last.Port)
                        || (dev.Host == last.Host && DeviceDiscoveryService.IsCompatible(dev)))
                    {
                        match = d;
                        break;
                    }
                }
                if (match?.Device is { } found)
                {
                    DispatcherQueue.TryEnqueue(() => _ = AutoStartFoundAsync(match, found));
                    return;
                }
                await Task.Delay(1000);
            }
            AppLog.Write("Auto-stream: last-used not found in 90s");
        });
    }

    private async Task AutoStartFoundAsync(DeviceItem match, AirNext.Core.Discovery.AirplayDevice found)
    {
        _suppressSelectionStart = true;
        _selectedDevice = found;
        DevicesList.SelectedItem = match;
        PlayingText.Text = "Selected: " + match.Name;
        _suppressSelectionStart = false;
        AppLog.Write("Auto-stream: found -> " + match.Name);
        await TryStartStreamingAsync(found);
    }

    private async Task TryStartStreamingAsync(AirNext.Core.Discovery.AirplayDevice device)
    {
        if (_isStreaming || _beam is null) return;
        if (!DeviceDiscoveryService.IsCompatible(device)) return;
        StartStopButton.IsEnabled = false;
        try
        {
            await _beam.StartAsync(device, autoMutePc: _settings.AutoMutePc);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Auto-start error: {ex.Message}");
            PlayingText.Text = "Connection failed";
        }
        StartStopButton.IsEnabled = true;
    }

    private void StartSearchStatusTimer()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(60000);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_discovery.Devices.Count == 0)
                {
                    SearchStatusText.Text = "No devices found. Make sure your AirPlay speaker is on the same network.";
                    AppLog.Write("SearchStatus: 60s with no devices");
                }
            });
        });
    }

    private void OnDevicesCountChanged(int count)
    {
        if (count > 0)
            SearchStatusText.Text = "";
    }

    private async void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DevicesList.SelectedItem is DeviceItem item && item.Device is not null)
        {
            _selectedDevice = item.Device;
            _discovery.SetLastUsed(item.Device);
            AppLog.Write($"Wybrano: {item.Device.FullName} ({item.Device.Host}:{item.Device.Port})");

            // Clicking a device starts streaming (if compatible)
            if (!_suppressSelectionStart && !_isStreaming && DeviceDiscoveryService.IsCompatible(item.Device))
            {
                await TryStartStreamingAsync(item.Device);
            }
        }
    }

    // --- RHI-170: BeamService integration ---

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming)
        {
            StartStopButton.IsEnabled = false;
            StartStopText.Text = "Stopping…";
            try { await _beam.StopAsync(); }
            catch (Exception ex) { AppLog.Write($"Stop error: {ex.Message}"); }
            StartStopButton.IsEnabled = true;
            StartStopText.Text = "Start";
            return;
        }

        var device = _selectedDevice;
        if (device is null)
        {
            PlayingText.Text = "Select a device first";
            return;
        }
        if (!DeviceDiscoveryService.IsCompatible(device))
        {
            PlayingText.Text = "Device is not RAOP (AirPlay 1)";
            return;
        }
        StartStopText.Text = "Connecting…";
        await TryStartStreamingAsync(device);
        if (!_isStreaming)
            StartStopText.Text = "Start";
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_beam.IsPaused)
        {
            await _beam.ResumeAsync();
        }
        else if (_isStreaming)
        {
            await _beam.PauseAsync();
        }
    }

    private async void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        VolumeText.Text = ((int)e.NewValue).ToString();
        if (_isStreaming)
        {
            await _beam.SetVolumeAsync((int)e.NewValue);
        }
    }

    // --- RHI-171/172: Advanced panel (live metrics + logs + RealTime toggle) ---

    private TextBlock? _advLatency;
    private TextBlock? _advPps;
    private TextBlock? _advPackets;
    private TextBlock? _advBytes;
    private TextBlock? _advGlitches;
    private TextBlock? _advTiming;
    private TextBlock? _advLog;
    private TextBlock? _advNetLatency;
    private TextBlock? _advMode;
    private TextBlock? _advWasapiBuffer;
    private TextBlock? _advDelay;
    private TextBlock? _advAnchor;
    private TextBlock? _advBitrate;
    private TextBlock? _advDuration;
    private TextBlock? _advSeq;
    private TextBlock? _advEncrypt;
    private TextBlock? _advDevice;
    private TextBlock? _advVolume;
    private TextBlock? _advFormat;
    private TextBlock? _advMdns;
    private StreamMetrics? _lastMetrics;
    private ContentDialog? _advDialog;

    private async void AdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Write("Advanced clicked");

        _advLatency = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advPps = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advPackets = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advBytes = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advGlitches = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advTiming = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advNetLatency = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advMode = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advWasapiBuffer = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advDelay = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advAnchor = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advBitrate = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advDuration = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advSeq = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advEncrypt = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advDevice = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"), TextWrapping = TextWrapping.Wrap };
        _advVolume = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advFormat = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advMdns = new TextBlock { Text = "—", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        _advLog = new TextBlock
        {
            Text = "—",
            FontSize = 11,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
            TextWrapping = TextWrapping.NoWrap,
        };

        var metricsPanel = new StackPanel { Spacing = 6 };
        metricsPanel.Children.Add(MetricRow("Device", _advDevice!));
        metricsPanel.Children.Add(MetricRow("Latency (receiver)", _advLatency));
        metricsPanel.Children.Add(MetricRow("Network (NTP RTT/2)", _advNetLatency));
        metricsPanel.Children.Add(MetricRow("A/V delay (ext.)", _advDelay!));
        metricsPanel.Children.Add(MetricRow("Sync 84 anchor", _advAnchor!));
        metricsPanel.Children.Add(MetricRow("WASAPI buffer", _advWasapiBuffer));
        metricsPanel.Children.Add(MetricRow("Packets/s", _advPps));
        metricsPanel.Children.Add(MetricRow("Bitrate", _advBitrate!));
        metricsPanel.Children.Add(MetricRow("Total packets", _advPackets));
        metricsPanel.Children.Add(MetricRow("Bytes sent", _advBytes));
        metricsPanel.Children.Add(MetricRow("Duration", _advDuration!));
        metricsPanel.Children.Add(MetricRow("RTP sequence", _advSeq!));
        metricsPanel.Children.Add(MetricRow("AES", _advEncrypt!));
        metricsPanel.Children.Add(MetricRow("Volume", _advVolume!));
        metricsPanel.Children.Add(MetricRow("ALAC format", _advFormat!));
        metricsPanel.Children.Add(MetricRow("Glitches (resend 85)", _advGlitches));
        metricsPanel.Children.Add(MetricRow("Timing 82→83", _advTiming));
        metricsPanel.Children.Add(MetricRow("Mode", _advMode));
        metricsPanel.Children.Add(MetricRow("mDNS RX / devices", _advMdns!));

        var logScroll = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        logScroll.Content = _advLog;

        var autoStream = new ToggleSwitch { Header = "Auto-stream last device on launch", IsOn = _settings.AutoStreamLastDevice, FontSize = 12 };
        autoStream.Toggled += (_, _) => { _settings.AutoStreamLastDevice = autoStream.IsOn; _settings.Save(); };

        var autoMute = new ToggleSwitch
        {
            Header = "Mute PC speakers while streaming (silences loopback too — use a virtual cable)",
            IsOn = _settings.AutoMutePc,
            FontSize = 12,
        };
        autoMute.Toggled += (_, _) => { _settings.AutoMutePc = autoMute.IsOn; _settings.Save(); };

        var startWin = new ToggleSwitch { Header = "Start BeamCast with Windows", IsOn = _settings.StartWithWindows, FontSize = 12 };
        startWin.Toggled += (_, _) => { _settings.StartWithWindows = startWin.IsOn; _settings.Save(); };

        var settingsPanel = new StackPanel { Spacing = 8 };
        settingsPanel.Children.Add(autoStream);
        settingsPanel.Children.Add(autoMute);
        settingsPanel.Children.Add(startWin);

        var metricsScroll = new ScrollViewer { MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        metricsScroll.Content = metricsPanel;

        var content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                BuildSection("Settings", settingsPanel),
                BuildSection("Stream stats", metricsScroll),
                BuildSection("Recent log", logScroll),
            },
        };

        _advDialog = new ContentDialog
        {
            Title = "Advanced — BeamCast v0.1",
            Content = content,
            PrimaryButtonText = "Close",
            XamlRoot = Content.XamlRoot,
            Width = 560,
        };

        UpdateAdvMetrics();
        UpdateAdvLog();
        await _advDialog.ShowAsync();

        _advDialog = null;
        _advLatency = null;
        _advPps = null;
        _advPackets = null;
        _advBytes = null;
        _advGlitches = null;
        _advTiming = null;
        _advNetLatency = null;
        _advMode = null;
        _advWasapiBuffer = null;
        _advDelay = null;
        _advAnchor = null;
        _advBitrate = null;
        _advDuration = null;
        _advSeq = null;
        _advEncrypt = null;
        _advDevice = null;
        _advVolume = null;
        _advFormat = null;
        _advMdns = null;
        _advLog = null;
    }

    private static UIElement BuildSection(string title, UIElement child)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 120, 120)),
        });
        panel.Children.Add(child);
        return panel;
    }

    private static UIElement MetricRow(string label, TextBlock valueBlock)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = label + ":",
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 154, 160, 170)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        valueBlock.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(valueBlock);
        return panel;
    }

    private void UpdateAdvMetrics()
    {
        if (_advLatency is null) return;
        var m = _lastMetrics;
        _advMdns!.Text = $"{_discovery.PacketsReceived:N0} pkt / {_discovery.Devices.Count} devices";
        if (m is null)
        {
            _advLatency.Text = "—";
            return;
        }
        _advDevice!.Text = string.IsNullOrEmpty(m.DeviceName) ? "—" : $"{m.DeviceName} ({m.DeviceEndpoint})";
        _advLatency.Text = m.LatencyMs > 0 ? $"{m.LatencyMs:F1} ms" : "— (receiver silent)";
        _advNetLatency!.Text = m.NetworkLatencyMs >= 0 ? $"{m.NetworkLatencyMs:F1} ms" : "unavailable";
        _advDelay!.Text = $"{m.DelayMs:F0} ms";
        _advAnchor!.Text = $"{m.AnchorMs} ms";
        _advWasapiBuffer!.Text = $"{m.WasapiBufferMs} ms";
        _advPps!.Text = $"{m.PacketsPerSecond:F1}";
        _advBitrate!.Text = $"{m.BitrateKbps:F1} kb/s";
        _advPackets!.Text = m.TotalPackets.ToString("N0");
        _advBytes!.Text = FormatBytes(m.BytesSent);
        var sec = (int)m.StreamSeconds;
        _advDuration!.Text = $"{sec / 60:D2}:{sec % 60:D2}";
        _advSeq!.Text = m.RtpSequence.ToString();
        _advEncrypt!.Text = m.Encrypted ? "on" : "off (et=0)";
        _advVolume!.Text = $"{m.VolumePercent} %";
        _advFormat!.Text = m.SampleRate > 0 ? $"{m.SampleRate} Hz / frame {m.FrameSize}" : "—";
        _advGlitches!.Text = m.GlitchCount.ToString();
        _advTiming!.Text = m.TimingReplies.ToString();
        _advMode!.Text = m.RealTimeMode ? $"RealTime ({m.AnchorMs} ms)" : "Normal (3 s anchor)";
    }

    private void UpdateAdvLog()
    {
        if (_advLog is null) return;
        try
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeamCast", "beancast.log");
            if (System.IO.File.Exists(logPath))
            {
                var lines = System.IO.File.ReadLines(logPath).ToList();
                var tail = lines.Skip(Math.Max(0, lines.Count - 40)).ToList();
                _advLog.Text = string.Join("\n", tail);
            }
            else
            {
                _advLog.Text = "(no log file yet)";
            }
        }
        catch (Exception ex)
        {
            _advLog.Text = $"(error reading log: {ex.Message})";
        }
    }

    // Surowe metryki z BeamService (do Advanced panel)
    private long _beamPacketsTotal;
    private long _beamBytesSent;
    private long _beamTimingReplies;

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };

    private void OnBeamStatusChanged(string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PlayingText.Text = status;

            if (status.StartsWith("Streaming"))
            {
                StartStopText.Text = "Stop";
                ConnectionState.Text = "Connected";
                ConnectionState.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 163, 74));
                StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 163, 74));
                MuteText.Text = _settings.AutoMutePc ? "PC muted ✓" : "";
            }
            else if (status == "Paused")
            {
                ConnectionState.Text = "Paused";
                ConnectionState.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 217, 119, 6));
                StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 217, 119, 6));
            }
            else if (status.StartsWith("Connecting") || status.StartsWith("Stopping"))
            {
                StartStopText.Text = status.StartsWith("Connecting") ? "Connecting…" : "Stopping…";
            }
            else
            {
                StartStopText.Text = "Start";
                ConnectionState.Text = "Disconnected";
                ConnectionState.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 26, 26));
                StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 26, 26));
                MuteText.Text = "";
                MetricsText.Text = "Latency — · 0 pkt/s · 0 glitches";
            }
        });
    }

    private double _beamNetworkLatencyMs = -1;
    private int _beamWasapiBufferMs;
    private bool _beamRealTimeMode;

    private void OnBeamMetricsUpdated(StreamMetrics m)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            string modeTag = m.RealTimeMode ? " [RT]" : "";
            string netLatency = m.NetworkLatencyMs >= 0 ? $"{m.NetworkLatencyMs:F0}ms" : "—";
            MetricsText.Text = $"Delay {m.DelayMs:F0}ms · Net {netLatency} · {m.PacketsPerSecond:F0} pkt/s · {m.GlitchCount} glitches{modeTag}";
            _lastMetrics = m;
            _beamPacketsTotal = m.TotalPackets;
            _beamBytesSent = m.BytesSent;
            _beamTimingReplies = m.TimingReplies;
            _beamNetworkLatencyMs = m.NetworkLatencyMs;
            _beamWasapiBufferMs = m.WasapiBufferMs;
            _beamRealTimeMode = m.RealTimeMode;
            UpdateAdvMetrics();
            UpdateAdvLog();
        });
    }
}

public enum DeviceStatus
{
    Active,
    Incompatible,
    Offline,
}

/// <summary>Device list row — from AirplayDevice (RHI-169).</summary>
public class DeviceItem
{
    public required string Name { get; init; }
    public required string Ip { get; init; }
    public required string StatusText { get; init; }
    public required DeviceStatus Status { get; init; }

    public AirNext.Core.Discovery.AirplayDevice? Device { get; init; }

    public SolidColorBrush? StatusBrush => Status switch
    {
        DeviceStatus.Active => new SolidColorBrush(Windows.UI.Color.FromArgb(20, 61, 220, 132)),
        DeviceStatus.Incompatible => new SolidColorBrush(Windows.UI.Color.FromArgb(20, 245, 166, 35)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(12, 120, 120, 120)),
    };

    public SolidColorBrush? StatusForeground => Status switch
    {
        DeviceStatus.Active => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 163, 74)),
        DeviceStatus.Incompatible => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 217, 119, 6)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 107, 114, 128)),
    };
}
