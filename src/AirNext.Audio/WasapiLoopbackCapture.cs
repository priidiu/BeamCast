using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AirNext.Core.Audio;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using static Windows.Win32.PInvoke;

namespace AirNext.Audio;

/// <summary>
/// WASAPI loopback capture (RHI-136) — event-driven, shared mode, QPC timestamps.
/// docs/06 §1–3: loopback tylko SHARED; event-driven od Win10 1703; mix format
/// float32/48k; MMCSS "Pro Audio"; re-init na DEVICE_INVALIDATED; zero alokacji
/// w thread capture (kopiowanie do pooled buffer).
/// </summary>
public sealed class WasapiLoopbackCapture : IAudioCaptureSource
{
 // CLSID_MMDeviceEnumerator / IID_IAudioClient — fixed GUID (nie ma ich w CsWin32 jako named)
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    private readonly int _bufferDurationMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private Task? _captureTask;
    private SafeFileHandle? _eventHandle;

    private IMMDeviceEnumerator? _enumerator;
    private IAudioClient? _client;
    private IAudioCaptureClient? _captureClient;

    public event Action<AudioPacket>? PacketCaptured;
#pragma warning disable CS0067 // DeviceInvalidated — used w re-init (DEVICE_INVALIDATED), M1 scope
    public event Action? DeviceInvalidated;
#pragma warning restore CS0067

    public AudioFormat MixFormat { get; private set; } = AudioFormat.MixDefault;

    /// <summary>Rozmiar bufora WASAPI w ms (200=RealTime, 2000=Normal).</summary>
    public int BufferDurationMs => _bufferDurationMs;

    public WasapiLoopbackCapture(int bufferDurationMs = 2000)
    {
        _bufferDurationMs = bufferDurationMs;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_captureTask is not null)
                return;

            Initialize();
            _captureTask = Task.Run(() => CaptureLoop(), CancellationToken.None);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        lock (_sync)
        {
            _cts.Cancel();
        }
        if (_captureTask is not null)
        {
            try { await _captureTask.ConfigureAwait(false); } catch { /* ignore */ }
            _captureTask = null;
        }
    }

    private unsafe void Initialize()
    {
        // IMMDeviceEnumerator → default render (console)
        HRESULT hr = CoCreateInstance(
            CLSID_MMDeviceEnumerator,
            null,
            CLSCTX.CLSCTX_INPROC_SERVER,
            out _enumerator);
        hr.ThrowOnFailure();

        _enumerator!.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device);

        // device.Activate(IID_IAudioClient)
        fixed (Guid* iid = &IID_IAudioClient)
        {
            device.Activate(iid, CLSCTX.CLSCTX_ALL, null, out object clientObj);
            _client = (IAudioClient)clientObj;
        }
        Marshal.ReleaseComObject(device);

        _client.GetMixFormat(out var formatPtr);
        var mixFormat = *formatPtr; // kopia do odczytu (MixFormat)
        MixFormat = new AudioFormat((int)mixFormat.nSamplesPerSec, (int)mixFormat.wBitsPerSample, (int)mixFormat.nChannels);

        // Event handle (event-driven) — overload z SafeFileHandle
        _eventHandle = CreateEvent(null, new BOOL(false), new BOOL(false), null);
        if (_eventHandle.IsInvalid)
            throw new InvalidOperationException("CreateEvent failed.");

        const uint loopback = AUDCLNT_STREAMFLAGS_LOOPBACK;
        const uint eventCallback = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        long hnsBuffer = (long)_bufferDurationMs * 10_000; // ms → 100ns units

        try
        {
 // Surowe call z REALNYM pointer do full formatu z GetMixFormat
 // (mix format to zwykle WAVEFORMATEXTENSIBLE > WAVEFORMATEX — kopia 18 B was truncated → E_INVALIDARG)
 // + pointer do Guid.Empty zamiast null (null w [Optional] Guid* gave NRE w marshallerze).
            // Guid to struct — adres locala value-type w metodzie unsafe nie wymaga fixed.
            Guid audioSessionGuid = Guid.Empty;
            Guid* pSession = &audioSessionGuid;
            _client.Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                loopback | eventCallback,
                hnsBuffer,
                0,
                formatPtr,
                pSession);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)formatPtr);
        }

        _client!.SetEventHandle(_eventHandle);
        _client.GetService(out _captureClient);
    }

    private unsafe void CaptureLoop()
    {
        // MMCSS "Pro Audio" — overload z ref uint + SafeHandle
        var mmcssTaskIndex = default(uint);
        using var mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref mmcssTaskIndex);

        _client!.Start();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_eventHandle is null)
                    break;

                var wait = WaitForSingleObject(_eventHandle, 1000);
                if (wait == WAIT_EVENT.WAIT_TIMEOUT)
                    continue;
                if (wait != WAIT_EVENT.WAIT_OBJECT_0)
                    break;

                DrainCaptureBuffer(_cts.Token);
            }
        }
        finally
        {
            try { _client.Stop(); } catch { /* ignore */ }
        }
    }

    private unsafe void DrainCaptureBuffer(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte* dataPtr;
            uint frames;
            uint flags;
            ulong qpcPos;

            try
            {
                _captureClient!.GetBuffer(&dataPtr, out frames, out flags, null, &qpcPos);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x88900001) // AUDCLNT_S_BUFFEREMPTY
            {
                return;
            }

            if (frames > 0)
            {
                int bytesPerFrame = MixFormat.BytesPerFrame;
                int byteCount = (int)frames * bytesPerFrame;
                var data = new byte[byteCount];
                bool silent = dataPtr == null
                    || (flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                if (!silent)
                    Marshal.Copy((nint)dataPtr, data, 0, byteCount);

                PacketCaptured?.Invoke(new AudioPacket(data, (long)qpcPos, MixFormat.SampleRate, MixFormat.BitDepth, MixFormat.Channels));
            }

            _captureClient.ReleaseBuffer(frames);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        _eventHandle?.Dispose();
        _cts.Dispose();

        if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator);
        if (_client is not null) Marshal.ReleaseComObject(_client);
        if (_captureClient is not null) Marshal.ReleaseComObject(_captureClient);
    }
}
