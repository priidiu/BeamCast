namespace AirNext.Core.Audio;

/// <summary>
/// Dithering TPDF (triangular probability density function) — float32 [-1,1] → int16.
/// Docs/06 §4: required przed truncation do 16 bits (unika artifacts kwantyzacji).
/// Deterministyczny dla tests: you can pass fixed seed (produkcja: Random.Shared).
/// </summary>
public static class Dither
{
    /// <summary>float32 [-1,1] → int16 z ditheringiem TPDF.</summary>
    public static short[] FloatToInt16(ReadOnlySpan<float> input, Random? rng = null)
    {
        rng ??= Random.Shared;
        var output = new short[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            // TPDF: (r1 - r2), zakres [-1, 1] → amplituda 1 LSB przy skali 32768
            double tpdf = rng.NextDouble() - rng.NextDouble();
            double scaled = input[i] * 32767.0 + tpdf;

            // clamp do zakresu int16
            int v = (int)Math.Round(scaled);
            v = Math.Clamp(v, short.MinValue, short.MaxValue);
            output[i] = (short)v;
        }

        return output;
    }
}
