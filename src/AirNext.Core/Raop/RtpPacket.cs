namespace AirNext.Core.Raop;

/// <summary>
/// Budowa packets RTP dla RAOP (docs/04 §4 + docs/wireshark-analysis.md §4–5).
///
/// Z przechwytu TuneBlade→WiiM Mini:
///  - Audio (PT 96): 12 B header (V=2, M, PT, seq, timestamp, SSRC) + payload ALAC.
///  - Sync (PT 84): 20 B — header 8 B bez SSRC (V/PT, seq, RTP ts) + NTP (8 B) + next RTP ts (4 B).
///  - Marker: sync zawsze M=1; audio M=1 na pierwszym pakiecie po RECORD/FLUSH.
/// </summary>
public static class RtpPacket
{
    public const byte PayloadTypeAudio = 96;
    public const byte PayloadTypeSync = 84;
    public const byte PayloadTypeTimingRequest = 82;
    public const byte PayloadTypeTimingReply = 83;
    public const byte PayloadTypeResendRequest = 85;
    public const byte PayloadTypeResendReply = 86;

 /// <summary>Build an audio packet (payload 96). 12 B header with SSRC.</summary>
    public static byte[] BuildAudio(ushort seq, uint timestamp, uint ssrc, bool marker, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80; // V=2, P=0, X=0, CC=0
        packet[1] = (byte)((marker ? 1 : 0) << 7 | PayloadTypeAudio);
        WriteUInt16(packet, 2, seq);
        WriteUInt32(packet, 4, timestamp);
        WriteUInt32(packet, 8, ssrc);
        payload.CopyTo(packet.AsSpan(12));
        return packet;
    }

    /// <summary>
 /// Build a retransmission response (payload 86) — 4 B pre-header + original audio packet.
    /// Format from shairport-sync rtp.c: the receiver does `pktp += 4` before the RTP header.
    /// Pre-header: 4 bajty (w AirPlay 1: 0x00 0x00 0x00 0x00 — zarezerwowane).
    /// </summary>
    public static byte[] BuildResendReply(ReadOnlySpan<byte> originalAudioPacket)
    {
        var packet = new byte[4 + originalAudioPacket.Length];
        // pre-header: 4 B zer (zarezerwowane)
        originalAudioPacket.CopyTo(packet.AsSpan(4));
 // Replace PT na 86 (0x56) — marker bit zachowany
        packet[4 + 1] = (byte)((originalAudioPacket[1] & 0x80) | PayloadTypeResendReply);
        return packet;
    }

    /// <summary>
    /// Build a sync packet (84) — 20 B: 8 B header without SSRC + NTP (8 B) + next RTP ts (4 B).
 /// Sent raz na second na port control; Marker zawsze ustawiony (jak w przechwycie).
    /// </summary>
    public static byte[] BuildSync(ushort seq, uint rtpTimestamp, ulong ntpTimestamp, uint nextRtpTimestamp)
    {
        var packet = new byte[20];
        packet[0] = 0x80;
        packet[1] = (byte)(1 << 7 | PayloadTypeSync); // M=1, PT=84 → 0xD4
        WriteUInt16(packet, 2, seq);
        WriteUInt32(packet, 4, rtpTimestamp);
        WriteUInt64(packet, 8, ntpTimestamp);
        WriteUInt32(packet, 16, nextRtpTimestamp);
        return packet;
    }

 /// <summary>Konwersja DateTime.UtcNow → NTP 64-bit (sekundy od 1900 + fraction).</summary>
    public static ulong ToNtpTimestamp(DateTime utc)
    {
        var epoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double totalSeconds = (utc - epoch).TotalSeconds;
        ulong seconds = (ulong)totalSeconds;
        ulong fraction = (ulong)((totalSeconds - seconds) * 4294967296.0);
        return (seconds << 32) | fraction;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteUInt64(byte[] buffer, int offset, ulong value)
    {
        WriteUInt32(buffer, offset, (uint)(value >> 32));
        WriteUInt32(buffer, offset + 4, (uint)value);
    }

 /// <summary>Odczyta seq z header RTP (offset 2).</summary>
    public static ushort ReadSeq(ReadOnlySpan<byte> packet) =>
        (ushort)((packet[2] << 8) | packet[3]);

    /// <summary>Odczyta timestamp RTP (offset 4).</summary>
    public static uint ReadTimestamp(ReadOnlySpan<byte> packet) =>
        (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]);
}
