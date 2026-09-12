using AirNext.Core.Audio;
using Xunit;

namespace AirNext.Core.Tests;

public class AudioFormatTests
{
    [Fact]
    public void AlacCdQuality_Is16Bit44100Stereo()
    {
        var fmt = AudioFormat.AlacCdQuality;
        Assert.Equal(44100, fmt.SampleRate);
        Assert.Equal(16, fmt.BitDepth);
        Assert.Equal(2, fmt.Channels);
        Assert.Equal(4, fmt.BytesPerFrame); // 2 B * 2 ch
    }

    [Fact]
    public void SamplesToMilliseconds_ConvertsCorrectly()
    {
 // 2205 samples @ 44.1 kHz = 50 ms (example Audio-Latency ze spec nto)
        Assert.Equal(50.0, AudioFormat.SamplesToMilliseconds(2205, 44100), precision: 3);
 // 11025 samples @ 44.1 kHz = 250 ms
        Assert.Equal(250.0, AudioFormat.SamplesToMilliseconds(11025, 44100), precision: 3);
    }

    [Fact]
    public void MillisecondsToSamples_RoundTrips()
    {
        long samples = AudioFormat.MillisecondsToSamples(50.0, 44100);
        Assert.Equal(2205, samples);
    }
}
