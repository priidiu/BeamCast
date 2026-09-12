using AirNext.Core.Audio;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>Testy LatencyMeasurer (RHI-140) — xcorr + threshold PASS/FAIL.</summary>
public class LatencyMeasurerTests
{
    [Fact]
    public void FindDelay_WithKnownDelay_ReturnsExactSamples()
    {
        const int rate = 44100;
        var sweep = LatencyMeasurer.GenerateSweep(rate, seconds: 1.0);

 // Symulacja: nagranie = sweep shifted o 882 samples (20 ms @44.1k)
        const int delay = 882;
        var captured = new float[sweep.Length + delay + 2000];
        sweep.CopyTo(captured, delay);

        // maxDelaySamples ≤ captured.Length - reference.Length (inaczej FindDelay zwraca -1)
        int maxDelay = captured.Length - sweep.Length; // = delay + 2000
        int found = LatencyMeasurer.FindDelay(sweep, captured, maxDelaySamples: maxDelay);
        Assert.Equal(delay, found);
        Assert.Equal(20.0, LatencyMeasurer.DelayMs(found, rate), precision: 3);
        Assert.True(LatencyMeasurer.IsPass(LatencyMeasurer.DelayMs(found, rate)));
    }

    [Fact]
    public void FindDelay_MultipleLatencies_AllMeasured()
    {
        const int rate = 48000;
        var sweep = LatencyMeasurer.GenerateSweep(rate, seconds: 0.8);

 // Threshold 40 ms @48k = 1920 samples; cel 10–40 ms
        int[] delays = { 480, 960, 1440, 1920 }; // 10, 20, 30, 40 ms
        foreach (var delay in delays)
        {
            var captured = new float[sweep.Length + delay + 1000];
            sweep.CopyTo(captured, delay);
            int maxDelay = captured.Length - sweep.Length;
            int found = LatencyMeasurer.FindDelay(sweep, captured, maxDelaySamples: maxDelay);
            Assert.Equal(delay, found);
            double ms = LatencyMeasurer.DelayMs(found, rate);
 // Kryterium M1: 10–40 ms; 1920 = exactly 40.0 ms — granica dopuszczalna
            Assert.True(ms is >= 10 and <= 40, $"delay {delay} → {ms:F1} ms poza zakresem 10–40");
        }
    }

    [Fact]
    public void FindDelay_WithNoiseOnly_ReturnsMinusOne()
    {
        var noise = new float[16000];
        var rng = new Random(42);
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(rng.NextDouble() * 2 - 1);

        var sweep = LatencyMeasurer.GenerateSweep(44100, seconds: 0.5);
 // Nagranie to czysty szum — brak signal → -1 (brak peaku)
        int found = LatencyMeasurer.FindDelay(sweep, noise, maxDelaySamples: 2000);
        Assert.Equal(-1, found);
        Assert.False(LatencyMeasurer.IsPass(LatencyMeasurer.DelayMs(found, 44100)));
    }

    [Fact]
    public void FindDelay_WithSignalAndNoise_ToleratesNoise()
    {
        const int rate = 44100;
        var sweep = LatencyMeasurer.GenerateSweep(rate, seconds: 1.0);
        const int delay = 500;
        var captured = new float[sweep.Length + delay + 1500];
        sweep.CopyTo(captured, delay);

 // Dodaj szum ±0.2 (sweep 0.5 amplitude) — peak musi survive
        var rng = new Random(7);
        for (int i = 0; i < captured.Length; i++)
            captured[i] += (float)(rng.NextDouble() * 0.4 - 0.2);

        int maxDelay = captured.Length - sweep.Length;
        int found = LatencyMeasurer.FindDelay(sweep, captured, maxDelaySamples: maxDelay);
        Assert.Equal(delay, found);
    }

    [Fact]
    public void FindDelay_SilenceBeforeSignal_StillFindsPeak()
    {
 // Realny scenariusz self-testu: nagranie ma silence PRZED sweepem (delay startu render)
        const int rate = 44100;
        var sweep = LatencyMeasurer.GenerateSweep(rate, seconds: 1.0);
 const int silenceBefore = 5000; // ~113 ms ciszy przed signal (jak delay render)
        var captured = new float[silenceBefore + sweep.Length + 2000];
        sweep.CopyTo(captured, silenceBefore);

        int maxDelay = captured.Length - sweep.Length;
        int found = LatencyMeasurer.FindDelay(sweep, captured, maxDelaySamples: maxDelay);
        Assert.Equal(silenceBefore, found);
    }

    [Fact]
    public void FindDelayWithStrength_ReturnsPeakRatio()
    {
        const int rate = 44100;
        var sweep = LatencyMeasurer.GenerateSweep(rate, seconds: 1.0);
        const int delay = 882;
        var captured = new float[sweep.Length + delay + 2000];
        sweep.CopyTo(captured, delay);

        int maxDelay = captured.Length - sweep.Length;
        var result = LatencyMeasurer.FindDelayWithStrength(sweep, captured, maxDelaySamples: maxDelay);
        Assert.Equal(delay, result.DelaySamples);
 // Silny signal: peak powinien be clearly ponad mean korelacji
        Assert.True(result.PeakRatio > 2.0, $"PeakRatio={result.PeakRatio:F1} — signal too weak");
    }

    [Fact]
    public void Sweep_IsValidSignal()
    {
        var sweep = LatencyMeasurer.GenerateSweep(44100, seconds: 1.0);
        Assert.Equal(44100, sweep.Length);
        // Amplituda w zakresie
        Assert.All(sweep, s => Assert.InRange(s, -0.6f, 0.6f));
 // Nie jest silence
        double energy = 0;
        foreach (var s in sweep) energy += s * s;
        Assert.True(energy > 1000, "sweep should have substantial energy");
    }

    [Fact]
    public void DelayMs_And_IsPass_Boundaries()
    {
        Assert.Equal(0.0, LatencyMeasurer.DelayMs(0, 44100));
        Assert.Equal(10.0, LatencyMeasurer.DelayMs(441, 44100), precision: 3);
        Assert.True(LatencyMeasurer.IsPass(39.9));
 Assert.False(LatencyMeasurer.IsPass(40.0)); // threshold < 40 ms
        Assert.False(LatencyMeasurer.IsPass(41.0));
        Assert.False(LatencyMeasurer.IsPass(double.NaN));
        Assert.False(LatencyMeasurer.IsPass(-5.0));
    }
}
