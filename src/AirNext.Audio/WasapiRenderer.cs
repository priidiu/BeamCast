using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using static Windows.Win32.PInvoke;

namespace AirNext.Audio;

/// <summary>
/// WASAPI renderer (RHI-140) — odtwarzanie float32 interleaved przez shared mode,
/// event-driven. Used przez self-test latencji (zamiast SoundPlayer — ten had
/// ~700 ms inicjalizacji, skewing pomiar).
/// </summary>
public sealed class WasapiRenderer : IDisposable
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private Task? _renderTask;
    private SafeFileHandle? _eventHandle;

    private IMMDeviceEnumerator? _enumerator;
    private IAudioClient? _client;
    private IAudioRenderClient? _renderClient;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    /// <summary>
 /// Inicjalizuje renderer w MIX FORMACIE device (GetMixFormat — jak capture).
 /// W shared mode z EVENTCALLBACK podawanie own formatu ≠ mix daje
    /// AUDCLNT_E_INVALID_DEVICE_PERIOD (0x88890008). Po Open() czytaj SampleRate/Channels.
    /// </summary>
    public void Open()
    {
        lock (_sync)
        {
            if (_client is not null)
                return;
            InitializeMixFormat();
        }
    }

 /// <summary>Odtwarza samples float32 interleaved w mix formacie (po Open()).</summary>
    public void Start(ReadOnlySpan<float> samples)
    {
        lock (_sync)
        {
            if (_renderTask is not null)
                return;

 var samplesCopy = samples.ToArray(); // Span nie may be w lambdzie
            _renderTask = Task.Run(() => RenderLoop(samplesCopy), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        lock (_sync) { _cts.Cancel(); }
        if (_renderTask is not null)
        {
            try { await _renderTask.ConfigureAwait(false); } catch { /* ignore */ }
            _renderTask = null;
        }
    }

    private unsafe void InitializeMixFormat()
    {
        HRESULT hr = CoCreateInstance(
            CLSID_MMDeviceEnumerator,
            null,
            CLSCTX.CLSCTX_INPROC_SERVER,
            out _enumerator);
        hr.ThrowOnFailure();

        _enumerator!.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device);
        fixed (Guid* iid = &IID_IAudioClient)
        {
            device.Activate(iid, CLSCTX.CLSCTX_ALL, null, out object clientObj);
            _client = (IAudioClient)clientObj;
        }
        Marshal.ReleaseComObject(device);

 // MIX FORMAT device (jak capture) — w shared mode render musi go use
        _client.GetMixFormat(out var formatPtr);
        var mixFormat = *formatPtr;
        SampleRate = (int)mixFormat.nSamplesPerSec;
        Channels = (int)mixFormat.nChannels;

        _eventHandle = CreateEvent(null, new BOOL(false), new BOOL(false), null);
        if (_eventHandle.IsInvalid)
            throw new InvalidOperationException("CreateEvent failed.");

        // EVENTCALLBACK wymaga niezerowego hnsPeriodicity (inaczej AUDCLNT_E_INVALID_DEVICE_PERIOD 0x88890008)
        _client.GetDevicePeriod(out long defaultPeriod, out _);
        long hnsPeriod = Math.Max(defaultPeriod, 10_000_000); // >= 10 ms (typowy default ~10 ms)
        long hnsBuffer = Math.Max(hnsPeriod * 2, 20_000_000); // >= 2× period, min 20 ms

        const uint eventCallback = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;

        Guid audioSessionGuid = Guid.Empty;
        Guid* pSession = &audioSessionGuid;
        try
        {
            _client.Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                eventCallback,
                hnsBuffer,
                hnsPeriod,
                formatPtr,
                pSession);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)formatPtr);
        }

        _client!.SetEventHandle(_eventHandle);
        _client.GetService(out _renderClient);
    }

    private unsafe void RenderLoop(float[] samples)
    {
        _client!.Start();
        int frameSize = Channels;
        int totalFrames = samples.Length / frameSize;
        int written = 0;

        try
        {
            while (!_cts.IsCancellationRequested && written < totalFrames)
            {
                if (_eventHandle is null) break;
                WaitForSingleObject(_eventHandle, 1000);

                _client.GetBufferSize(out uint bufferFrames);
                _client.GetCurrentPadding(out uint padding);
                uint available = bufferFrames - padding;
                if (available == 0)
                    continue;

                uint toWrite = Math.Min(available, (uint)(totalFrames - written));
                if (toWrite == 0)
                    break;

                _renderClient!.GetBuffer(toWrite, out byte* data);
                if (data is null)
                {
                    _renderClient!.ReleaseBuffer(toWrite, (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT);
                    continue;
                }

                var dst = new Span<float>(data, (int)(toWrite * frameSize));
                samples.AsSpan(written * frameSize, dst.Length).CopyTo(dst);
                _renderClient!.ReleaseBuffer(toWrite, 0); // AUDCLNT_BUFFERFLAGS_NONE
                written += (int)toWrite;
            }

 // Ogonek: send silence until do zatrzymania? Nie — we stop, gdy samples end.
        }
        finally
        {
            try { _client.Stop(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
 // 0x88890001 = AUDCLNT_S_BUFFEREMPTY (kod sukcesu z bitem error — COM rzuca COMException)
 // Drugi Stop() na zatrzymanym kliencie zwraca ten kod — we catch.
 try { _client?.Stop(); } catch (COMException) { /* BUFFEREMPTY — klient already zatrzymany */ }
        _eventHandle?.Dispose();
        if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator);
        _cts.Dispose();
    }
}
