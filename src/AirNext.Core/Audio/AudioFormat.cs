namespace AirNext.Core.Audio;

/// <summary>
/// Opis formatu audio w chain przetwarzania (docs/06 §4).
/// Wzorzec: mix systemowy float32/48k → target (ALAC 16/44.1 baseline, 24/48 cel).
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int BitDepth, int Channels)
{
    public static AudioFormat MixDefault { get; } = new(48000, 32, 2);

    public static AudioFormat AlacCdQuality { get; } = new(44100, 16, 2);

    public static AudioFormat AlacHighRes { get; } = new(48000, 24, 2);

    public int BytesPerFrame => (BitDepth / 8) * Channels;

 /// <summary>Latencja w samples → milisekundy.</summary>
    public static double SamplesToMilliseconds(long samples, int sampleRate) =>
        (double)samples * 1000.0 / sampleRate;

 /// <summary>Odwrotnie: ms → samples.</summary>
    public static long MillisecondsToSamples(double milliseconds, int sampleRate) =>
        (long)(milliseconds * sampleRate / 1000.0);
}
