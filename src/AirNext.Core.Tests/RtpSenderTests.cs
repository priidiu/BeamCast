using System.Net;
using System.Net.Sockets;
using AirNext.Core.Raop;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>
/// M1 end-to-end (RHI-139): RTSP handshake → RTP audio (payload 96) → mock receiver.
/// Values startowe (seq=35853, rtptime=16441947, SSRC=0x34249563) z przechwytu TuneBlade→WiiM Mini.
/// </summary>
public class RtpSenderTests
{
    private static byte[] FakeAlacPayload(uint rtptime)
    {
 // 4 B header ALAC-over-RTP (jak w przechwycie: 20 00 00 04) + dane
        var payload = new byte[4 + 348];
        payload[0] = 0x20;
        payload[1] = 0x00;
        payload[2] = 0x00;
        payload[3] = 0x04;
        for (int i = 0; i < 348; i++)
            payload[4 + i] = (byte)(i + (int)(rtptime & 0xFF));
        return payload;
    }

    [Fact]
    public async Task AudioStream_ReachesMockReceiver_WithExpectedSeqAndCount()
    {
        await using var receiver = MockRaopReceiver.Start();
        await using var session = await RaopSession.ConnectAsync("127.0.0.1", receiver.Port);

        const string path = "stream1";
        await session.AnnounceAsync(path);
        await session.SetupAsync(path);
        await session.RecordAsync(path, seq: 35853, rtptime: 16441947);

        receiver.StartRtpListener(session.ServerPort);

        var sender = new RtpSender(ssrc: 0x34249563);
        sender.Connect(IPAddress.Loopback, session.ServerPort, session.ControlPort);

        using (sender)
        {
 // Send 100 audio packets (352 samples/packet @ 44.1 kHz) + 2 sync
            await sender.StreamAsync(
                startSeq: 35853,
                startRtptime: 16441947,
                samplesPerPacket: 352,
                sampleRate: 44100,
                packetCount: 100,
                FakeAlacPayload);

            sender.SendSync(rtptime: 16441947 + 352 * 100, nextRtptime: 16441947 + 352 * 101);
            sender.SendSync(rtptime: 16441947 + 352 * 101, nextRtptime: 16441947 + 352 * 102);
        }

 // Czekamy until UDP dotrze (loopback = natychmiast, ale dajmy a moment na przetworzenie)
        await Task.Delay(200);

        Assert.Equal(100, receiver.RtpPacketCount);
    }

    [Fact]
    public void RtpAudioPacket_HasCorrectHeader()
    {
        var payload = new byte[] { 0x20, 0x00, 0x00, 0x04, 1, 2, 3 };
        var packet = RtpPacket.BuildAudio(seq: 35853, timestamp: 16441947, ssrc: 0x34249563, marker: true, payload);

        Assert.Equal(12 + payload.Length, packet.Length);
        Assert.Equal(0x80, packet[0]);          // V=2
        Assert.Equal(0xE0, packet[1]);          // M=1, PT=96
        Assert.Equal(0x8C, packet[2]);          // seq hi = 35853 >> 8
        Assert.Equal(0x0D, packet[3]);          // seq lo
        Assert.Equal(0x34, packet[8]);          // SSRC hi
        Assert.Equal(0x63, packet[11]);         // SSRC lo
    }

    [Fact]
    public void SyncPacket_Is20Bytes_WithMarkerAndNtp()
    {
        var packet = RtpPacket.BuildSync(seq: 7, rtpTimestamp: 0x0181D86B, ntpTimestamp: 0x83AA80C06A7EF9DB, nextRtpTimestamp: 0x0183DD37);

        Assert.Equal(20, packet.Length);
        Assert.Equal(0x80, packet[0]);
        Assert.Equal(0xD4, packet[1]);          // M=1, PT=84
        Assert.Equal(0x83, packet[8]);          // NTP seconds hi
        Assert.Equal(0x01, packet[16]);         // next RTP ts hi
    }

    [Fact]
    public async Task TimingServer_RespondsTo82_With83Reply()
    {
        using var sender = new RtpSender(ssrc: 0x34249563);
        sender.Connect(IPAddress.Loopback, 51000, 51001);
        sender.StartTimingServer();

        // Request 82 jak WiiM: 80 d2 00 07 + 4B RTP ts (0) + 8B NTP originate
        var request = new byte[20];
        request[0] = 0x80;
        request[1] = 0xD2; // M=1, PT=82
        request[2] = 0x00;
        request[3] = 0x07;
        var ntp = RtpPacket.ToNtpTimestamp(new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc));
        WriteUInt64BE(request, 12, ntp);

        using var client = new UdpClient();
        await client.SendAsync(request, new IPEndPoint(IPAddress.Loopback, sender.TimingPort));

        var response = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var reply = response.Buffer;

        Assert.Equal(32, reply.Length);
        Assert.Equal(0x80, reply[0]);
        Assert.Equal(0xD3, reply[1]); // M=1, PT=83
        Assert.Equal(0x00, reply[2]);
        Assert.Equal(0x07, reply[3]);
        // originate = NTP z requestu (echo)
        Assert.Equal(ReadUInt64BE(reply, 8), ntp);
        // receive + transmit = teraz (niezerowe, ~ten sam czas)
        Assert.Equal(ReadUInt64BE(reply, 16), ReadUInt64BE(reply, 24));
    }

    [Fact]
    public void SendAudio_BeforeConnect_Throws()
    {
 // Regresja: send przed Connect went na port 0 (bug znaleziony przez pcap!) — musi throw
        using var sender = new RtpSender(ssrc: 0x34249563);
        Assert.Throws<InvalidOperationException>(() =>
            sender.SendAudio(1, 1000, new byte[] { 1, 2, 3 }));
        Assert.Throws<InvalidOperationException>(() =>
            sender.SendSync(1000, 2000));
    }

    private static void WriteUInt64BE(byte[] buffer, int offset, ulong value)
    {
        buffer[offset] = (byte)(value >> 56);
        buffer[offset + 1] = (byte)(value >> 48);
        buffer[offset + 2] = (byte)(value >> 40);
        buffer[offset + 3] = (byte)(value >> 32);
        buffer[offset + 4] = (byte)(value >> 24);
        buffer[offset + 5] = (byte)(value >> 16);
        buffer[offset + 6] = (byte)(value >> 8);
        buffer[offset + 7] = (byte)value;
    }

    private static ulong ReadUInt64BE(byte[] buffer, int offset) =>
        ((ulong)buffer[offset] << 56) | ((ulong)buffer[offset + 1] << 48) |
        ((ulong)buffer[offset + 2] << 40) | ((ulong)buffer[offset + 3] << 32) |
        ((ulong)buffer[offset + 4] << 24) | ((ulong)buffer[offset + 5] << 16) |
        ((ulong)buffer[offset + 6] << 8) | buffer[offset + 7];
}
