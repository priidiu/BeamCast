namespace AirNext.Core.Audio;

/// <summary>
/// FormatConverter (RHI-137) — konwersja przechwyconego audio do formatu AirPlay.
///
/// Pipeline (profil z testu RHI-136: mix 96000 Hz / 32 bit / 8 ch → ALAC 16/44.1 stereo):
///   input (interleaved float32, N ch, inRate)
///     → Downmixer.ToStereo (N→2)
/// → Resampler.Resample (inRate→outRate, per channel)
///     → Dither.FloatToInt16 (TPDF)
///     → output (interleaved int16, 2 ch, outRate)
///
/// All of it czysty C# — testowalna na every OS (Linux CI), bez dependencies natywnych.
/// </summary>
public static class FormatConverter
{
 /// <summary>Konwertuje interleaved float32 ([-1,1], N channels) do interleaved int16 stereo.</summary>
    public static short[] ConvertToPcm16Stereo(
        ReadOnlySpan<float> input,
        int inputChannels,
        int inputSampleRate,
        int outputSampleRate = 44100,
        Random? rng = null)
    {
        // 1) Downmix N → stereo (float32, interleaved)
        var stereo = Downmixer.ToStereo(input, inputChannels);

        if (inputSampleRate == outputSampleRate)
            return Dither.FloatToInt16(stereo, rng);

 // 2) Resampling per channel (de-interleave → resample → re-interleave)
        int frames = stereo.Length / 2;
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = stereo[i * 2];
            right[i] = stereo[i * 2 + 1];
        }

        var resampledL = Resampler.Resample(left, inputSampleRate, outputSampleRate);
        var resampledR = Resampler.Resample(right, inputSampleRate, outputSampleRate);

        int outFrames = resampledL.Length;
        var interleaved = new float[outFrames * 2];
        for (int i = 0; i < outFrames; i++)
        {
            interleaved[i * 2] = resampledL[i];
            interleaved[i * 2 + 1] = resampledR[i];
        }

        // 3) Dithering TPDF → int16
        return Dither.FloatToInt16(interleaved, rng);
    }
}
