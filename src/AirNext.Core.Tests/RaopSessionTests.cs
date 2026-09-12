using AirNext.Core.Raop;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>
/// Testy integracyjne RaopSession ↔ MockRaopReceiver — verify flow z przechwytu
/// TuneBlade→WiiM Mini (docs/wireshark-analysis.md).
/// </summary>
public class RaopSessionTests
{
    [Fact]
    public async Task FullHandshake_AuthSetupToRecord_MatchesWiiMBehaviour()
    {
        await using var receiver = MockRaopReceiver.Start(audioLatencySamples: null); // WiiM: brak Audio-Latency
        await using var session = await RaopSession.ConnectAsync("127.0.0.1", receiver.Port);

        const string path = "3121287335"; // jak w przechwycie

        await session.AuthSetupAsync();
        await session.OptionsAsync();
        await session.AnnounceAsync(path);
        await session.SetupAsync(path);
        await session.RecordAsync(path, seq: 35853, rtptime: 16441947);

 // WiiM nie zwraca Audio-Latency — sesja musi work bez niej
        Assert.Null(session.AudioLatencySamples);

        // Porty przydzielone przez mock (dynamiczne — jak prawdziwy WiiM)
        Assert.Equal(receiver.ServerPort, session.ServerPort);
        Assert.Equal(receiver.ControlPort, session.ControlPort);
        Assert.Equal(receiver.TimingPort, session.TimingPort);
        Assert.Equal("1", session.SessionId);

        // Method sequence in the receiver log
        var log = string.Join("\n", receiver.Log);
        Assert.Contains("POST /auth-setup", log);
        Assert.Contains("OPTIONS *", log);
        Assert.Contains("ANNOUNCE", log);
        Assert.Contains("SETUP", log);
        Assert.Contains("RECORD", log);
        Assert.Contains("AppleLossless", log);
        Assert.Contains("a=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100", log);
    }

    [Fact]
    public async Task AudioLatency_WhenConfigured_IsExposed()
    {
        await using var receiver = MockRaopReceiver.Start(audioLatencySamples: 2205); // 50 ms @44.1k
        await using var session = await RaopSession.ConnectAsync("127.0.0.1", receiver.Port);

        const string path = "s1";
        await session.AnnounceAsync(path);
        await session.SetupAsync(path);
        await session.RecordAsync(path, seq: 1, rtptime: 1000);

        Assert.Equal(2205, session.AudioLatencySamples);
    }

    [Fact]
    public async Task Teardown_ClosesGracefully()
    {
        await using var receiver = MockRaopReceiver.Start();
        var session = await RaopSession.ConnectAsync("127.0.0.1", receiver.Port);

        const string path = "s2";
        await session.AnnounceAsync(path);
        await session.SetupAsync(path);
        await session.RecordAsync(path, seq: 2, rtptime: 2000);
        await session.TeardownAsync(path);

        Assert.Contains("TEARDOWN", string.Join("\n", receiver.Log));
    }
}
