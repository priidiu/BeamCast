namespace AirNext.Core.Audio;

/// <summary>
/// Resampler windowed-sinc (Lanczos-3) — czysty C#, deterministyczny, testowalny na every OS.
/// Docs/09 recommended SoXr (P/Invoke); ten resampler jest na M1 (prototyp) i jako fallback —
/// quality comparable dla audio, zero dependencies natywnych.
/// </summary>
public static class Resampler
{
 private const int Taps = 12; // Lanczos-3: 2*3 = 6 po each stronie → kernel 12

    /// <summary>
 /// Resampling mono/pojedynczego channel float32.
 /// inRate → outRate; stosunek may be dowolny (96k→44.1k itd.).
    /// </summary>
    public static float[] Resample(ReadOnlySpan<float> input, int inRate, int outRate)
    {
        if (inRate == outRate)
            return input.ToArray();

        double ratio = (double)outRate / inRate;
        int outLen = (int)Math.Ceiling(input.Length * ratio);
        var output = new float[outLen];

 // Pozycja w channels source: each sample output ma "idealny" indeks source
        for (int o = 0; o < outLen; o++)
        {
 double srcPos = o / ratio; // pozycja continuous w input
            int center = (int)Math.Floor(srcPos);
            double frac = srcPos - center;

            double sum = 0;
            double norm = 0;
            int half = Taps / 2;

            for (int t = -half + 1; t <= half; t++)
            {
                int idx = center + t;
                if (idx < 0 || idx >= input.Length)
                    continue;

                double x = t - frac;
                double w = Lanczos3(x);
                sum += input[idx] * w;
                norm += w;
            }

            output[o] = norm > 0 ? (float)(sum / norm) : 0f;
        }

        return output;
    }

    private static double Lanczos3(double x)
    {
        if (x == 0)
            return 1.0;
        double ax = Math.Abs(x);
        if (ax >= 3)
            return 0.0;
        return 3.0 * Math.Sin(Math.PI * x) * Math.Sin(Math.PI * x / 3.0) / (Math.PI * Math.PI * x * x);
    }
}
