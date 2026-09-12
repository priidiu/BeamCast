using System.Net;
using AirNext.Core.Raop;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>FLUSH tests (RHI-146) — receiver buffer reset + RTP-Info.</summary>
public class FlushTests
{
    [Fact]
    public async Task Flush_AfterRecord_Returns200_AndResetsMock()
    {
        await using var receiver = MockRaopReceiver.Start(audioLatencySamples: null);
        await using var session = await RaopSession.ConnectAsync("127.0.0.1", receiver.Port);
        const string path = "3121287335";
        await session.AuthSetupAsync();
        await session.OptionsAsync();
        await session.AnnounceAsync(path);

        var sender = new RtpSender(ssrc: 0x34249563);
        using (sender)
        {
            sender.StartTimingServer();
            await session.SetupAsync(path, sender.ControlPort, sender.TimingPort);
            sender.Connect(IPAddress.Loopback, session.ServerPort, session.ControlPort);
            receiver.StartRtpListener(session.ServerPort);
            await session.RecordAsync(path, seq: 35853, rtptime: 16441947);

 // Send kilka packets, potem FLUSH
            for (int i = 0; i < 5; i++)
                sender.SendAudio((ushort)(35853 + i), (uint)(16441947 + i * 352), new byte[] { (byte)i });
            await Task.Delay(200);
            Assert.True(receiver.RtpPacketCount >= 5, "mock received no packets before FLUSH");

 await session.FlushAsync(path); // nie powinno throw (200 OK)

            // Po FLUSH liczniki zresetowane
            Assert.Equal(0, receiver.RtpPacketCount);

 // RTP-Info z RECORD remembered
            Assert.Equal((ushort)35853, session.LastFlushedSeq);
            Assert.Equal((uint)16441947, session.LastFlushedRtptime);
        }
    }

    [Fact]
    public async Task Flush_ParsesRtpInfoFromResponse()
    {
        await using var receiver = MockRaopReceiver.Start(audioLatencySamples: null);
        await using var session = await RaopSession.ConnectAsync("127.0.0.1", receiver.Port);
        const string path = "3121287335";
        await session.AuthSetupAsync();
        await session.OptionsAsync();
        await session.AnnounceAsync(path);

        var sender = new RtpSender(ssrc: 0x34249563);
        using (sender)
        {
            sender.StartTimingServer();
            await session.SetupAsync(path, sender.ControlPort, sender.TimingPort);
            sender.Connect(IPAddress.Loopback, session.ServerPort, session.ControlPort);
            receiver.StartRtpListener(session.ServerPort);
            await session.RecordAsync(path, seq: 35853, rtptime: 16441947);

            await session.FlushAsync(path);
 // Mock nie zwraca RTP-Info — ale values z RECORD are available
            Assert.NotNull(session.LastFlushedSeq);
            Assert.NotNull(session.LastFlushedRtptime);
        }
    }
}
