using AirNext.Core.Audio;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>Testy FormatConverter / Downmixer / Resampler / Dither (RHI-137 cz. A).</summary>
public class FormatConverterTests
{
    [Fact]
    public void Downmix_StereoInput_PassthroughPreservesEnergy()
    {
        // Stereo: L=0.8, R=-0.8 → po normalizacji ~0.566/-0.566
        float[] input = { 0.8f, -0.8f, 0.8f, -0.8f };
        var stereo = Downmixer.ToStereo(input, 2);

        Assert.Equal(4, stereo.Length);
        Assert.Equal(0.8f * 0.7071067811865476f, stereo[0], 4);
        Assert.Equal(-0.8f * 0.7071067811865476f, stereo[1], 4);
    }

    [Fact]
    public void Downmix_71_Map_Channels_Correctly()
    {
        // 7.1: FL=1, FR=1, FC=1, LFE=1, BL=1, BR=1, SL=1, SR=1
        float[] input = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        var stereo = Downmixer.ToStereo(input, 8);

        // L = 1 + 0.707 + 0.707 + 0.707 + 0.5 = 3.621; *0.707 = 2.56 (clamp przed ditheringiem robi FormatConverter)
        double l = 1 + 0.7071067811865476 * 3 + 0.5;
        Assert.Equal(l * 0.7071067811865476, stereo[0], 3);
        Assert.Equal(l * 0.7071067811865476, stereo[1], 3); // symetria
    }

    [Fact]
    public void Resampler_SameRate_IsPassthrough()
    {
        float[] input = { 0.1f, -0.2f, 0.3f, -0.4f };
        var output = Resampler.Resample(input, 44100, 44100);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Resampler_96kTo44_1k_PreservesLengthRatio()
    {
 // 9600 samples @96k → 4410 @44.1k (exact stosunek 96/44.1 = 480/220.5 ≈ 2.1769)
        var input = new float[9600];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 96000.0); // 440 Hz @96k

        var output = Resampler.Resample(input, 96000, 44100);
        Assert.Equal(4410, output.Length);
    }

    [Fact]
    public void Resampler_Sine440_Stays440AfterDownsample()
    {
 // Sinus 440 Hz @96k → resample 44.1k → policz zero-crossing i zweryfikuj frequency
        int n = 96000; // 1 s
        var input = new float[n];
        for (int i = 0; i < n; i++)
            input[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 96000.0);

        var output = Resampler.Resample(input, 96000, 44100);

 // Zero-crossing (rising) w 1 s sinusa 440 Hz = 440
        int crossings = 0;
        for (int i = 1; i < output.Length; i++)
        {
            if (output[i - 1] < 0 && output[i] >= 0)
                crossings++;
        }

        Assert.InRange(crossings, 430, 450);
    }

    [Fact]
    public void Dither_Int16_ClampsToRange()
    {
        var rng = new Random(42);
        float[] input = { 2.0f, -2.0f, 0.5f }; // przesterowane + normalne
        var output = Dither.FloatToInt16(input, rng);

        Assert.Equal(short.MaxValue, output[0]);
        Assert.Equal(short.MinValue, output[1]);
        Assert.InRange(output[2], short.MinValue, short.MaxValue);
    }

    [Fact]
    public void Dither_Deterministic_WithSeed()
    {
        float[] input = { 0.1f, 0.2f, 0.3f, 0.4f };
        var a = Dither.FloatToInt16(input, new Random(123));
        var b = Dither.FloatToInt16(input, new Random(123));
        Assert.Equal(a, b);
    }

    [Fact]
    public void FormatConverter_FullPipeline_96k8ch_To_44k1Stereo16()
    {
        // Profil z testu RHI-136: 96k/32/8ch → 44.1k/16/2ch
        int channels = 8;
        int frames = 4800; // 0.05 s @96k
        var input = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            double t = i / 96000.0;
            // FL=sin(440), FR=sin(440), reszta cisza
            input[i * channels + 0] = (float)Math.Sin(2 * Math.PI * 440 * t);
            input[i * channels + 1] = (float)Math.Sin(2 * Math.PI * 440 * t);
        }

        var output = FormatConverter.ConvertToPcm16Stereo(input, channels, 96000, 44100, new Random(7));

        // 4800 frames @96k → 2205 frames @44.1k stereo
        Assert.Equal(2205 * 2, output.Length);

 // Signal nie jest silence (energia w lewym kanale)
        long sumAbs = 0;
        for (int i = 0; i < 2205; i += 10)
            sumAbs += Math.Abs(output[i * 2]);
        Assert.True(sumAbs > 0, "Signal should not be silence after conversion.");
    }
}
