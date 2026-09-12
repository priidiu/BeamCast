namespace AirNext.Core.Audio;

/// <summary>
/// Captured audio packet: raw samples + QPC timestamp of the first frame.
/// QPC timestamp jest basis synchronizacji A/V (docs/05 §2): WASAPI daje go
/// przez pu64QPCPosition w GetBuffer (IAudioClock / IAudioCaptureClient).
/// </summary>
public readonly record struct AudioPacket(
    ReadOnlyMemory<byte> Data,
    long QpcTimestamp,
    int SampleRate,
    int BitDepth,
    int Channels)
{
 /// <summary>Packet latency in ms vs a given "now" QPC (xcorr self-test).</summary>
    public double LatencyMs(long nowQpc, long qpcFrequency) =>
        (nowQpc - QpcTimestamp) * 1000.0 / qpcFrequency;
}

/// <summary>
/// Abstrakcja source przechwytywania audio. Implementacja Windows: WasapiLoopbackCapture
/// (projekt AirNext.Audio). Testowalna headless — mock w testach (RHI-140 self-test xcorr).
/// </summary>
public interface IAudioCaptureSource : IAsyncDisposable
{
 /// <summary>New audio packet event (capture thread — do not block!).</summary>
    event Action<AudioPacket>? PacketCaptured;

 /// <summary>Zdarzenie przy zmianie device (DEVICE_INVALIDATED) — wymaga re-init.</summary>
    event Action? DeviceInvalidated;

    /// <summary>Start przechwytywania (event-driven).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop przechwytywania.</summary>
    Task StopAsync();

    /// <summary>Aktualny format miksu (z GetMixFormat).</summary>
    AudioFormat MixFormat { get; }
}
