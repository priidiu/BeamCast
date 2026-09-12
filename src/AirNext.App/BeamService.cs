using System.Net;
using System.Runtime.InteropServices;
using AirNext.Audio;
using AirNext.Core.Audio;
using AirNext.Core.Discovery;
using AirNext.Core.Raop;
using Microsoft.UI.Dispatching;

namespace AirNext_App;

/// <summary>
/// AirPlay stream orchestrator (RHI-170): WASAPI capture → FormatConverter → AlacEncoder → RTP → receiver.
/// Wzorzec z StreamProbe/Program.cs — uproszczony do UI (start/stop/pause, volume, auto-mute).
/// </summary>
public sealed class BeamService : IAsyncDisposable
{
    private const string SessionPath = "3121287335";
    private const int SampleRate = 44100;
    private const int FrameSize = 352;
    private const int NormalBufferMs = 2000;
    private const int NormalAnchorMs = 3000;

    private readonly DispatcherQueue _uiDispatcher;
    private RaopSession? _session;
    private RtpSender? _sender;
    private WasapiLoopbackCapture? _capture;
    private AlacEncoder? _encoder;
    private CancellationTokenSource? _cts;

    private ushort _seq;
    private uint _rtptime;
    private readonly short[] _pcmBuffer = new short[FrameSize * 2];
    private int _pcmFrames;
    private long _packetsSent;
    private long _bytesSent;
    private bool _paused;
    private bool _encrypt;
    private DateTime _streamStartedUtc;
    private string _deviceName = "";
    private string _deviceEndpoint = "";
    private int _volumePercent = 62;

    // Auto-mute PC (ADR-005)
    private bool _pcMuted;
    private float _savedVolume = -1f;
    private IAudioEndpointVolume? _endpointVolume;

    // Status events (UI thread)
    public event Action<string>? StatusChanged;
    public event Action<StreamMetrics>? MetricsUpdated;
    public event Action? StreamingStarted;
    public event Action? StreamingStopped;

    public bool IsStreaming => _session is not null && !_paused;
    public bool IsPaused => _paused;

    /// <summary>RealTime: WASAPI + sync 84 = RealTimeDelayMs. Normal: 2000ms / 3s.</summary>
    public bool RealTimeMode { get; set; }

    /// <summary>User-configured A/V delay (20–250 ms). Applied to WASAPI (at start) and sync 84 (live).</summary>
    public int RealTimeDelayMs { get; set; } = 40;

    private int ActiveAnchorMs => RealTimeMode ? Math.Clamp(RealTimeDelayMs, 20, 250) : NormalAnchorMs;
    private int ActiveBufferMs => RealTimeMode ? Math.Clamp(RealTimeDelayMs, 20, 250) : NormalBufferMs;

    public BeamService(DispatcherQueue uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
    }

    /// <summary>Start streaming to an AirPlay device.</summary>
    public async Task StartAsync(AirplayDevice device, bool autoMutePc = true)
    {
        if (_session is not null)
            throw new InvalidOperationException("Stream already running.");

        _encrypt = device.RequiresEncryption;
        StatusChanged?.Invoke("Connecting...");

        try
        {
            // 1. Sesja RAOP
            _session = await RaopSession.ConnectAsync(device.Host, device.Port);
            AppLog.Write($"BeamService: TCP connected to {device.Host}:{device.Port}");

            try
            {
                await _session.AuthSetupAsync();
                AppLog.Write("BeamService: auth-setup OK");
            }
            catch (Exception ex)
            {
                AppLog.Write($"BeamService: auth-setup failed: {ex.Message} (may still work)");
            }

            await _session.OptionsAsync();
            await _session.AnnounceAsync(SessionPath, encrypt: _encrypt);

            // 2. Sender RTP
            _sender = new RtpSender(ssrc: 0x34249563);
            _sender.StartTimingServer();

            await _session.SetupAsync(SessionPath, _sender.ControlPort, _sender.TimingPort);
            _sender.Connect(IPAddress.Parse(device.Host), _session.ServerPort, _session.ControlPort, _session.TimingPort);

            _seq = 35853;
            _rtptime = 16441947;
            await _session.RecordAsync(SessionPath, _seq, _rtptime);

            // Sync 84: RealTime = slider, Normal 3 s
            uint anchorSamples = (uint)(SampleRate * ActiveAnchorMs / 1000);
            uint anchor = _rtptime >= anchorSamples ? _rtptime - anchorSamples : 0;
            _sender.SendSync(anchor, anchor + anchorSamples);

            // SET_PARAMETER volume + metadata + progress (jak StreamProbe)
            try { await _session.SetParameterAsync(SessionPath, "volume: -0.001000"); } catch { }
            try
            {
                var metadata = DmapMetadataBuilder.Build("System Audio", "BeamCast");
                await _session.SetParameterAsync(SessionPath, metadata, "application/x-dmap-tagged", $"rtptime={_rtptime}");
            }
            catch { }
            try { await _session.SetParameterAsync(SessionPath, $"progress: {_rtptime}/{_rtptime}/{_rtptime}"); } catch { }

            // 3. Capture WASAPI
            int bufferMs = ActiveBufferMs;
            _encoder = new AlacEncoder(sampleRate: SampleRate, frameSize: FrameSize);
            _pcmFrames = 0;
            _packetsSent = 0;
            _bytesSent = 0;
            _cts = new CancellationTokenSource();

            _capture = new WasapiLoopbackCapture(bufferMs);
            AppLog.Write($"BeamService: WASAPI buffer = {bufferMs}ms ({(RealTimeMode ? "RealTime" : "Normal")} mode)");
            _capture.PacketCaptured += OnPacketCaptured;
            await _capture.StartAsync();

            if (autoMutePc)
                MutePc();

            _paused = false;
            _streamStartedUtc = DateTime.UtcNow;
            _deviceName = device.Name;
            _deviceEndpoint = $"{device.Host}:{device.Port}";
            StatusChanged?.Invoke($"Streaming to {device.Name}");
            StreamingStarted?.Invoke();
            AppLog.Write($"BeamService: streaming started to {device.Name} ({device.Host}:{device.Port})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"BeamService: start FAILED: {ex}");
            StatusChanged?.Invoke("Connection failed");
            await CleanupAsync();
            throw;
        }
    }

    /// <summary>Stop streaming.</summary>
    public async Task StopAsync()
    {
        if (_session is null) return;

        AppLog.Write("BeamService: stopping...");
        StatusChanged?.Invoke("Stopping...");

        // Zatrzymaj capture
        if (_capture is not null)
        {
            _capture.PacketCaptured -= OnPacketCaptured;
            await _capture.StopAsync();
            await _capture.DisposeAsync();
            _capture = null;
        }

        // Flush remainder (partial frame)
        if (_encoder is not null && _pcmFrames > 0)
            SendPartialFrame();

        // TEARDOWN
        try { await _session.TeardownAsync(SessionPath); } catch { }

        await CleanupAsync();
        StatusChanged?.Invoke("Disconnected");
        StreamingStopped?.Invoke();
        AppLog.Write("BeamService: stopped");
    }

    /// <summary>Pause (FLUSH — reset receiver buffer).</summary>
    public async Task PauseAsync()
    {
        if (_session is null || _paused) return;

        // Zatrzymaj capture
        if (_capture is not null)
        {
            _capture.PacketCaptured -= OnPacketCaptured;
            await _capture.StopAsync();
        }

        // Flush remaining buffer
        if (_encoder is not null && _pcmFrames > 0)
            SendPartialFrame();

        // FLUSH
        try { await _session.FlushAsync(SessionPath); } catch { }

        _paused = true;
        StatusChanged?.Invoke("Paused");
        AppLog.Write("BeamService: paused");
    }

    /// <summary>Resume after pause.</summary>
    public async Task ResumeAsync()
    {
        if (_session is null || !_paused) return;

        // RECORD z nowym seq/rtptime (po FLUSH)
        var seq = _session.LastFlushedSeq ?? _seq;
        var rtptime = _session.LastFlushedRtptime ?? _rtptime;
        await _session.RecordAsync(SessionPath, seq, rtptime);
        _seq = seq;
        _rtptime = rtptime;
        _pcmFrames = 0;

        // Resume capture
        if (_capture is not null)
        {
            _capture.PacketCaptured += OnPacketCaptured;
            await _capture.StartAsync();
        }

        _paused = false;
        StatusChanged?.Invoke("Streaming");
        AppLog.Write("BeamService: resumed");
    }

    /// <summary>Set volume (0-100) on the AirPlay receiver.</summary>
    public async Task SetVolumeAsync(int volume0100)
    {
        if (_session is null) return;
        _volumePercent = Math.Clamp(volume0100, 0, 100);

        // AirPlay volume: -144.0 (mute) do 0.0 (max) — dB w skali liniowej
        // -0.001 ≈ full, -144 = mute
        double db = volume0100 <= 0 ? -144.0 : -0.001 * (100.0 / volume0100);
        var body = $"volume: {db:F6}";

        try
        {
            await _session.SetParameterAsync(SessionPath, body);
        }
        catch (Exception ex)
        {
            AppLog.Write($"BeamService: volume failed: {ex.Message}");
        }
    }

    private void OnPacketCaptured(AudioPacket packet)
    {
        if (_cts is null || _cts.Token.IsCancellationRequested || _paused) return;

        try
        {
            // WASAPI: float32 interleaved → FormatConverter → int16 stereo 44.1k
            var floats = new float[packet.Data.Length / 4];
            var span = packet.Data.Span;
            for (int i = 0; i < floats.Length; i++)
                floats[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * 4, 4));

            var converted = FormatConverter.ConvertToPcm16Stereo(
                floats, packet.Channels, packet.SampleRate, SampleRate, rng: null);

            // Akumuluj do ramki ALAC
            int framesIn = converted.Length / 2;
            int src = 0;
            while (src < framesIn)
            {
                int take = Math.Min(FrameSize - _pcmFrames, framesIn - src);
                Array.Copy(converted, src * 2, _pcmBuffer, _pcmFrames * 2, take * 2);
                _pcmFrames += take;
                src += take;

                if (_pcmFrames == FrameSize)
                {
                    SendAlacFrame();
                    _pcmFrames = 0;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"BeamService: pipeline error: {ex.Message}");
        }
    }

    private void SendAlacFrame()
    {
        if (_encoder is null || _sender is null) return;

        var rtpOut = new byte[_encoder.MaxOutputBytes];
        int n = _encoder.EncodeFrame(_pcmBuffer, rtpOut);
        if (n <= 0) return;

        if (_encrypt)
            _sender.SendEncryptedAudio(_seq, _rtptime, rtpOut.AsSpan(0, n));
        else
            _sender.SendAudio(_seq, _rtptime, rtpOut.AsSpan(0, n), marker: false);

        _seq++;
        _rtptime += FrameSize;
        _packetsSent++;
        _bytesSent += n;

        // Sync 84 co ~1 s
        if (_packetsSent % 125 == 1)
        {
            uint anchorSamples = (uint)(SampleRate * ActiveAnchorMs / 1000);
            uint anchor = _rtptime >= anchorSamples ? _rtptime - anchorSamples : 0;
            _sender.SendSync(anchor, anchor + anchorSamples);
        }

        // Metrics (every ~100 packets ≈ 0.8 s)
        if (_packetsSent % 100 == 0)
        {
            var elapsed = Math.Max(0.001, (DateTime.UtcNow - _streamStartedUtc).TotalSeconds);
            var metrics = new StreamMetrics
            {
                LatencyMs = _session?.AudioLatencySamples * 1000.0 / SampleRate ?? 0,
                NetworkLatencyMs = _sender?.MeasuredLatencyMs ?? -1,
                RealTimeMode = RealTimeMode,
                WasapiBufferMs = _capture?.BufferDurationMs ?? 0,
                DelayMs = ActiveAnchorMs + Math.Max(0, _sender?.MeasuredLatencyMs ?? 0),
                AnchorMs = ActiveAnchorMs,
                PacketsPerSecond = _packetsSent / elapsed,
                BitrateKbps = _bytesSent * 8.0 / elapsed / 1000.0,
                BytesSent = _bytesSent,
                TotalPackets = _packetsSent,
                GlitchCount = _sender?.ResendRequestsHandled ?? 0,
                TimingReplies = _sender?.TimingRepliesSent ?? 0,
                RtpSequence = _seq,
                Encrypted = _encrypt,
                StreamSeconds = elapsed,
                DeviceName = _deviceName,
                DeviceEndpoint = _deviceEndpoint,
                VolumePercent = _volumePercent,
                SampleRate = SampleRate,
                FrameSize = FrameSize,
            };
            _uiDispatcher.TryEnqueue(() => MetricsUpdated?.Invoke(metrics));
        }
    }

    private void SendPartialFrame()
    {
        if (_encoder is null || _sender is null || _pcmFrames == 0) return;

        var partial = new short[_pcmFrames * 2];
        Array.Copy(_pcmBuffer, partial, _pcmFrames * 2);
        var rtpOut = new byte[_encoder.MaxOutputBytes];
        int n = _encoder.EncodeFrame(partial, rtpOut);
        if (n > 0)
        {
            if (_encrypt)
                _sender.SendEncryptedAudio(_seq, _rtptime, rtpOut.AsSpan(0, n));
            else
                _sender.SendAudio(_seq, _rtptime, rtpOut.AsSpan(0, n), marker: false);
            _packetsSent++;
            _bytesSent += n;
        }
    }

    // --- Auto-mute PC (ADR-005) ---

    private void MutePc()
    {
        try
        {
            _endpointVolume = GetDefaultAudioEndpointVolume();
            if (_endpointVolume is null) return;

            _endpointVolume.GetMasterVolumeLevelScalar(out _savedVolume);
            _endpointVolume.SetMasterVolumeLevelScalar(0f, Guid.Empty);
            _pcMuted = true;
            AppLog.Write($"BeamService: PC muted (saved vol={_savedVolume:F2})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"BeamService: auto-mute failed: {ex.Message}");
        }
    }

    private void UnmutePc()
    {
        if (!_pcMuted || _endpointVolume is null) return;

        try
        {
            if (_savedVolume >= 0f)
                _endpointVolume.SetMasterVolumeLevelScalar(_savedVolume, Guid.Empty);
            _pcMuted = false;
            AppLog.Write($"BeamService: PC unmuted (restored vol={_savedVolume:F2})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"BeamService: unmute failed: {ex.Message}");
        }
        finally
        {
            Marshal.ReleaseComObject(_endpointVolume);
            _endpointVolume = null;
        }
    }

    private static IAudioEndpointVolume? GetDefaultAudioEndpointVolume()
    {
        // MMDeviceEnumerator → default render (console) → IAudioEndpointVolume
        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        if (enumeratorType is null) return null;

        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
        try
        {
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var device);
            try
            {
                var volumeGuid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref volumeGuid, 0 /* CLSCTX_ALL */, IntPtr.Zero, out var volumeObj);
                return (IAudioEndpointVolume)volumeObj;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private async Task CleanupAsync()
    {
        UnmutePc();
        _cts?.Cancel();
        if (_capture is not null)
        {
            await _capture.DisposeAsync();
            _capture = null;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _sender?.Dispose();
        _sender = null;
        _encoder = null;
        _cts?.Dispose();
        _cts = null;
        _pcmFrames = 0;
        _paused = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    // --- COM interop for auto-mute (Core Audio API) ---

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // IUnknown: QueryInterface(0), AddRef(1), Release(2)
        // EnumerateAudioEndPoints(3)
        // GetDefaultAudioEndpoint(4)
        // GetDevice(5)
        // RegisterEndpointNotificationCallback(6)
        // UnregisterEndpointNotificationCallback(7)
        void EnumerateAudioEndPoints();
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        // IUnknown: QueryInterface(0), AddRef(1), Release(2)
        // OnEndpointVolumeChanged(3)
        // GetChannelCount(4)
        // GetMasterVolumeLevel(5)
        // GetMasterVolumeLevelScalar(6)
        // SetMasterVolumeLevelScalar(7)
        void OnEndpointVolumeChanged();
        void GetChannelCount();
        void GetMasterVolumeLevel();
        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig]
        int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
    }
}

public sealed class StreamMetrics
{
    public double LatencyMs { get; init; }
    public double NetworkLatencyMs { get; init; }
    public bool RealTimeMode { get; init; }
    public int WasapiBufferMs { get; init; }
    public double DelayMs { get; init; }
    public int AnchorMs { get; init; }
    public double PacketsPerSecond { get; init; }
    public double BitrateKbps { get; init; }
    public long TotalPackets { get; init; }
    public long BytesSent { get; init; }
    public long GlitchCount { get; init; }
    public long TimingReplies { get; init; }
    public int RtpSequence { get; init; }
    public bool Encrypted { get; init; }
    public double StreamSeconds { get; init; }
    public string DeviceName { get; init; } = "";
    public string DeviceEndpoint { get; init; } = "";
    public int VolumePercent { get; init; }
    public int SampleRate { get; init; }
    public int FrameSize { get; init; }
}

internal static class DmapMetadataBuilder
{
    public static byte[] Build(string title, string artist)
    {
        static byte[] DmapTag(string tag, string value)
        {
            var val = System.Text.Encoding.UTF8.GetBytes(value);
            var data = new byte[val.Length + 1];
            val.CopyTo(data, 0);
            var result = new byte[8 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(result, 0);
            result[4] = (byte)(data.Length >> 24);
            result[5] = (byte)(data.Length >> 16);
            result[6] = (byte)(data.Length >> 8);
            result[7] = (byte)data.Length;
            data.CopyTo(result, 8);
            return result;
        }

        var minm = DmapTag("minm", title);
        var asar = DmapTag("asar", artist);
        var asal = DmapTag("asal", "");

        int contentLen = minm.Length + asar.Length + asal.Length;
        var mlit = new byte[8 + contentLen];
        System.Text.Encoding.ASCII.GetBytes("mlit").CopyTo(mlit, 0);
        mlit[4] = (byte)(contentLen >> 24);
        mlit[5] = (byte)(contentLen >> 16);
        mlit[6] = (byte)(contentLen >> 8);
        mlit[7] = (byte)contentLen;
        minm.CopyTo(mlit, 8);
        asar.CopyTo(mlit, 8 + minm.Length);
        asal.CopyTo(mlit, 8 + minm.Length + asar.Length);

        return mlit;
    }
}
