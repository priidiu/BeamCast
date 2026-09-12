namespace AirNext.Core.Audio;

/// <summary>
/// LatencyMeasurer (RHI-140) — pomiar delay capture przez cross-correlation.
///
/// Metoda: wygeneruj signal testowy (sweep) → play przez loopback → nagraj
/// → xcorr nagrania z original → peak korelacji = delay w samples/ms.
/// Threshold akceptacji M1/NFR-1: 10–40 ms (PASS &lt; 40 ms).
///
/// Used w: testach jednostkowych (symulowane delay), CaptureProbe
/// (--latency-test, live hardware), ewentualnie autodiagnostyce przy starcie.
/// </summary>
public static class LatencyMeasurer
{
    /// <summary>
 /// Generuje sweep logarytmiczny (chirp) — signal testowy do loopback.
 /// Zakres frequency 200 Hz → 2 kHz (dobrze widoczny w xcorr).
    /// </summary>
    public static float[] GenerateSweep(int sampleRate, double seconds = 1.0, double f0 = 200.0, double f1 = 2000.0)
    {
        int n = (int)(sampleRate * seconds);
        var sweep = new float[n];
        // log-sweep: f(t) = f0 * (f1/f0)^(t/T); faza = 2π * ∫f dt
        double k = Math.Log(f1 / f0);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double frac = t / seconds;
            // ∫0^t f0*exp(k*τ/T) dτ = f0*T/k * (exp(k*t/T)-1)
            double phase = 2.0 * Math.PI * f0 * seconds / k * (Math.Exp(k * frac) - 1.0);
            sweep[i] = (float)(0.5 * Math.Sin(phase));
        }
        return sweep;
    }

    /// <summary>
 /// Znajduje delay (w samples) between signal referencyjnym a nagraniem
 /// przez cross-correlation w oknie lags [0, maxDelaySamples).
 /// Zwraca indeks peaku korelacji albo -1, gdy peak za weak (brak signal).
    /// </summary>
    public static int FindDelay(ReadOnlySpan<float> reference, ReadOnlySpan<float> captured, int maxDelaySamples)
    {
        if (reference.Length < 256 || captured.Length < reference.Length + maxDelaySamples)
            return -1;

 // Use segmentu referencji od START signal (pomijanie start broke xcorr —
 // sweep[refStart..] nie correlated z captured[delay..]).
 int useLen = reference.Length / 2; // half signal — enough energia, mniejsze ryzyko rampy trailing

        double best = double.MinValue;
        int bestDelay = -1;

 // xcorr brute-force: O(maxDelay × useLen) — dla 1 s @44.1k i 100 ms okna ≈ 4.4M multiplies
        for (int delay = 0; delay < maxDelaySamples; delay++)
        {
            double dot = 0;
            for (int i = 0; i < useLen; i++)
                dot += reference[i] * captured[delay + i];
            if (dot > best)
            {
                best = dot;
                bestDelay = delay;
            }
        }

 // Walidacja: peak musi be significantly ponad mean korelacji (signal obecny)
 // — prosty threshold: best > 0 (sweep dodatni samokorelacja); dla szumu peak ~0.
        if (best <= 0)
            return -1;

        return bestDelay;
    }

 /// <summary>Wynik xcorr z strength peaku (do diagnostyki false-peak).</summary>
    public readonly record struct DelayResult(int DelaySamples, double Peak, double Average, double PeakRatio);

    /// <summary>
 /// FindDelay + diagnostics: returns delay and peak-to-mean correlation ratio.
 /// PeakRatio &lt; ~2 sugeruje weak signal / false peak (np. sweep masked szumem).
    /// </summary>
    public static DelayResult FindDelayWithStrength(ReadOnlySpan<float> reference, ReadOnlySpan<float> captured, int maxDelaySamples)
    {
        if (reference.Length < 256 || captured.Length < reference.Length + maxDelaySamples)
            return new DelayResult(-1, 0, 0, 0);

        int useLen = reference.Length / 2;
        double best = double.MinValue;
        int bestDelay = -1;
        double sum = 0;

        for (int delay = 0; delay < maxDelaySamples; delay++)
        {
            double dot = 0;
            for (int i = 0; i < useLen; i++)
                dot += reference[i] * captured[delay + i];
            sum += Math.Abs(dot);
            if (dot > best)
            {
                best = dot;
                bestDelay = delay;
            }
        }

        if (best <= 0 || maxDelaySamples == 0)
            return new DelayResult(-1, best, maxDelaySamples > 0 ? sum / maxDelaySamples : 0, 0);

        double avg = sum / maxDelaySamples;
        double ratio = avg > 0 ? best / avg : double.PositiveInfinity;
        return new DelayResult(bestDelay, best, avg, ratio);
    }

 /// <summary>Delay w ms dla danego sample rate.</summary>
    public static double DelayMs(int delaySamples, int sampleRate) =>
        delaySamples < 0 ? double.NaN : (double)delaySamples * 1000.0 / sampleRate;

 /// <summary>Status PASS/FAIL vs threshold (NFR-1: 10–40 ms).</summary>
    public static bool IsPass(double delayMs, double thresholdMs = 40.0) =>
        !double.IsNaN(delayMs) && delayMs >= 0 && delayMs < thresholdMs;
}
