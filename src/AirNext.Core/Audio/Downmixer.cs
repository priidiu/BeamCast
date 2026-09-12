namespace AirNext.Core.Audio;

/// <summary>
/// Downmix multichannel → stereo (docs/06 §4 + wyniki testu RHI-136: mix 96k/32/8ch).
///
/// Mapowanie channels dla typowych layouts (order WASAPI/Windows):
///   0=FL, 1=FR, 2=FC, 3=LFE, 4=BL, 5=BR, 6=SL, 7=SR  (7.1)
///   0=FL, 1=FR, 2=FC, 3=LFE, 4=BL, 5=BR              (5.1)
///   0=FL, 1=FR, 2=FC, 3=SL, 4=SR                      (5.0)
///   0=FL, 1=FR                                        (stereo)
///
/// Wagi wg typowego downmixu ITU-R BS.775 (uproszczone):
///   L = FL + 0.707·FC + 0.707·BL + 0.707·SL + 0.5·LFE
///   R = FR + 0.707·FC + 0.707·BR + 0.707·SR + 0.5·LFE
/// Po sumowaniu normalizacja o 1/sqrt(2) — zapobiega przesterom przy full miksie.
/// </summary>
public static class Downmixer
{
    public const double CenterWeight = 0.7071067811865476; // 1/sqrt(2)
    public const double LfeWeight = 0.5;

    /// <summary>
    /// Downmix interleaved float32 ([-1,1]) do stereo (interleaved float32).
    /// </summary>
    public static float[] ToStereo(ReadOnlySpan<float> input, int channels)
    {
        int frames = input.Length / channels;
        var output = new float[frames * 2];

        for (int i = 0; i < frames; i++)
        {
            int src = i * channels;

            double l = src < input.Length ? input[src] : 0;      // FL
            double r = channels > 1 ? input[src + 1] : l;        // FR
            double fc = channels > 2 ? input[src + 2] : 0;       // FC
            double lfe = channels > 3 ? input[src + 3] : 0;      // LFE
            double bl = channels > 4 ? input[src + 4] : 0;       // BL
            double br = channels > 5 ? input[src + 5] : 0;       // BR
            double sl = channels > 6 ? input[src + 6] : 0;       // SL
            double sr = channels > 7 ? input[src + 7] : 0;       // SR

            double outL = l + CenterWeight * fc + CenterWeight * bl + CenterWeight * sl + LfeWeight * lfe;
            double outR = r + CenterWeight * fc + CenterWeight * br + CenterWeight * sr + LfeWeight * lfe;

 // Normalizacja: 1/sqrt(2) przy full miksie (4+ source po ~1.0 would give ~3.4)
            output[i * 2] = (float)(outL * 0.7071067811865476);
            output[i * 2 + 1] = (float)(outR * 0.7071067811865476);
        }

        return output;
    }
}
