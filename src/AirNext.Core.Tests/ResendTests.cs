using System.Net;
using AirNext.Core.Audio;
using AirNext.Core.Raop;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>
/// Testy retransmisji RTP 85/86 (RHI-143, M2):
/// RtpSender buffers packets → MockRaopReceiver drops 10% → sends 85 → RtpSender replies 86
///   → mock recovers every dropped packet (100/100).
/// </summary>
public class ResendTests
{
    [Fact]
    public async Task Sender_BuffersAudio_AndRepliesToResend()
    {
        // Mock z 10% strat
        await using var receiver = MockRaopReceiver.Start(audioLatencySamples: null, dropFraction: 0.10);
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
 receiver.SetSenderControlPort(sender.ControlPort); // gdzie send 85
            await session.RecordAsync(path, seq: 35853, rtptime: 16441947);

 // Send 100 packets audio (szybko — bez pacer, so nie wait)
            var payload = new byte[200];
            new Random(1).NextBytes(payload);
            for (int i = 0; i < 100; i++)
                sender.SendAudio((ushort)(35853 + i), (uint)(16441947 + i * 352), payload);

            await Task.Delay(200); // UDP dotrze

            int lostBefore = 100 - receiver.RtpPacketCount;
            Assert.True(lostBefore > 0, $"oczekiwano strat przy dropFraction=0.10 (odebrano {receiver.RtpPacketCount}/100)");
 // diagnostyka: ile seq tracks mock
            Assert.True(lostBefore < 50, $"too many losses: {lostBefore}");

 // Requesty 85 dla gaps
            await receiver.RequestResendsAsync();
            await Task.Delay(500); // odpowiedzi 86

            // diagnostyka
            Assert.True(receiver.ResendRecoveredCount > 0,
                $"no packet recovered via 86 (straty={lostBefore}, handled={sender.ResendRequestsHandled}, controlRx={receiver.ControlPacketsReceived})");
            Assert.True(receiver.RtpPacketCount >= 100,
                $"po retransmisji tylko {receiver.RtpPacketCount}/100 (odzyskano {receiver.ResendRecoveredCount})");
            Assert.True(sender.ResendRequestsHandled > 0, "RtpSender handled no type-85 request");
        }
    }

    [Fact]
    public void ResendReply_Has4BytePreHeader_AndPayloadType86()
    {
        // Original audio packet
        var original = RtpPacket.BuildAudio(seq: 1234, timestamp: 5000, ssrc: 0x34249563, marker: false, payload: new byte[] { 1, 2, 3 });
        var reply = RtpPacket.BuildResendReply(original);

        Assert.Equal(4 + original.Length, reply.Length);
        Assert.Equal(0x00, reply[0]); // pre-header
        Assert.Equal(0x00, reply[1]);
        Assert.Equal(0x00, reply[2]);
        Assert.Equal(0x00, reply[3]);
 // PT=86 w RTP headerze (offset 4+1); marker zachowany z original (0 → 0x56)
        Assert.Equal(RtpPacket.PayloadTypeResendReply, reply[5]);
        // seq zachowany (offset 4+2)
        Assert.Equal(0x04, reply[6]);
        Assert.Equal(0xD2, reply[7]);
        // payload zachowany
        Assert.Equal(new byte[] { 1, 2, 3 }, reply.AsSpan(16).ToArray());
    }
}
