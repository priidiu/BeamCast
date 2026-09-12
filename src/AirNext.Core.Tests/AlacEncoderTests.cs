using AirNext.Core.Audio;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>
/// Testy bit-exact enkodera ALAC (RHI-137 cz. B).
/// Golden vector wygenerowany ORYGINALNYM enkoderem Apple (ALACEncoder.cpp, Apache-2.0)
/// na Linuxie (roundtrip enc→dec bit-perfect, 9192 ramek: sinus + cisza + wieloton + partial ramka).
/// Pliki: tests/fixtures/golden_{input,output,cookie}.
/// </summary>
public class AlacEncoderTests
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory, "fixtures");

    private static short[] LoadGoldenInput()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, "golden_input.s16le"));
        var samples = new short[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return samples;
    }

    [Fact]
    public void MagicCookie_MatchesAppleReference()
    {
        var encoder = new AlacEncoder(sampleRate: 44100, frameSize: 4096);
        // maxFrameBytes w cookie to max z dotychczasowych ramek — golden generowany PO enkodowaniu
        var input = LoadGoldenInput();
        var output = new byte[encoder.MaxOutputBytes];
        int pos = 0;
        while (pos < input.Length / 2)
        {
            int frames = Math.Min(4096, input.Length / 2 - pos);
            encoder.EncodeFrame(input.AsSpan(pos * 2, frames * 2), output);
            pos += frames;
        }

        var cookie = encoder.GetMagicCookie();
        var golden = File.ReadAllBytes(Path.Combine(FixturesDir, "golden_cookie.bin"));

        Assert.Equal(golden.Length, cookie.Length);
        Assert.Equal(golden, cookie);
    }

    [Fact]
    public void EncodeFrame_BitExact_AgainstAppleReference()
    {
        var input = LoadGoldenInput();
        var encoder = new AlacEncoder(sampleRate: 44100, frameSize: 4096);
        var goldenFrames = File.ReadAllBytes(Path.Combine(FixturesDir, "golden_output.alac"));

        var output = new byte[encoder.MaxOutputBytes];
        var produced = new List<byte>();

        int pos = 0;
        while (pos < input.Length / 2)
        {
            int frames = Math.Min(4096, input.Length / 2 - pos);
            var frame = input.AsSpan(pos * 2, frames * 2);
            int n = encoder.EncodeFrame(frame, output);
            Assert.True(n > 0, $"EncodeFrame returned {n} for frame at {pos}");
            produced.AddRange(output.AsSpan(0, n).ToArray());
            pos += frames;
        }

        Assert.Equal(goldenFrames.Length, produced.Count);
        Assert.Equal(goldenFrames, produced.ToArray());
    }

    [Fact]
    public void EncodeFrame_EmptyInput_ReturnsError()
    {
        var encoder = new AlacEncoder();
        var output = new byte[encoder.MaxOutputBytes];
        Assert.Equal(-1, encoder.EncodeFrame(ReadOnlySpan<short>.Empty, output));
    }

    [Fact]
    public void EncodeFrame_TooLargeFrame_ReturnsError()
    {
        var encoder = new AlacEncoder(frameSize: 4096);
        var output = new byte[encoder.MaxOutputBytes];
        var big = new short[8194]; // 4097 frames > frameSize 4096
        Assert.Equal(-1, encoder.EncodeFrame(big, output));
    }
}
