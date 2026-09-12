using System.Security.Cryptography;

namespace AirNext.Core.Raop;

/// <summary>
/// AirPlay 1 audio encryption (RHI-144) — faithful TuneBlade port (decompilation,
/// initFixedEncryptionKeys + processPCMAndEnqueue):
///
/// - AES-128-CBC, Padding=None, FIXED klucz i IV (TuneBlade uses fixed keys)
///  - Packet layout: [0..3] resendHeader (pre-header) + [4..15] RTP header
///    + [16..] ALAC payload zaszyfrowany CBC (od offsetu 16)
/// - SDP: a=rsaaeskey:&lt;constant&gt; + a=aesiv:&lt;base64 IV&gt;
///
/// NOTE: this is TuneBlade encryption (fixed keys) — we do not generate our own
/// RSA/AES na session. Dla WiiM (et=0, plaintext) nie jest used; enabled
/// only for receivers that require encryption (et=1).
/// </summary>
public static class AesEncryption
{
 // Constants klucze z TuneBlade initFixedEncryptionKeys() (linie 35526-35538 dekompilacji)
    public static readonly byte[] FixedKey = { 11, 160, 214, 176, 146, 6, 210, 20, 248, 213, 154, 240, 159, 171, 187, 21 };
    public static readonly byte[] FixedIv = { 48, 124, 13, 0, 96, 67, 200, 7, 244, 245, 203, 81, 65, 45, 168, 194 };

 /// <summary>a=rsaaeskey — RSA-encrypted AES key (constant z TuneBlade).</summary>
    public const string RsaaesKey =
        "1phMbunVTkyWKOASlg6vYVDN/q/YyjIHJa+yFkLcIuT/CoSbBye3cYqy1Iy/byeFr6YFVfqMM4tkvCdauWCB+KeDjcxk84JTieuwcjIMN2dpuBPye45egTw5h01PB8yI4HJgOWJvwylqM+YDwq7NfpApYvzFG4GnFAoxk9j5AKKbifaP538IzYt5PlCrVLpNeWfEMkAEad82SZs7qoxgSHeL0+mB6YLDLtocfQuP9fb64im+0/Xc4dwgUfYte6vETlCbHH+Uh5Dij71QDrjFipl+gjm29DqBx9rWEw53gL5f3vHL1SNM6KpNGKTGifeC0Dlwu+EUKl44zA2aidxfGg";

    /// <summary>Base64 IV — a=aesiv w SDP.</summary>
    public static string IvBase64 => Convert.ToBase64String(FixedIv);

    /// <summary>
    /// Build an encrypted audio packet: [4 B pre-header][12 B RTP][ALAC CBC].
    /// Wierny port TuneBlade: pre-header = resendHeader (type=86, seq=packetSize/4),
    /// payload encrypted AES-128-CBC from offset 16.
    /// </summary>
    public static byte[] EncryptAudioPacket(ushort seq, uint timestamp, uint ssrc, ReadOnlySpan<byte> alacPayload)
    {
        int packetSize = alacPayload.Length + 12;
        var packet = new byte[4 + packetSize];

 // Pre-header (resendHeader): type=RangeResendResponse(86), seqNum = packetSize/4 (rounded w up)
        packet[0] = 0x80;
        packet[1] = (byte)(0x80 | RtpPacket.PayloadTypeResendReply); // M=1, PT=86
        ushort hdrSeq = (ushort)((packetSize / 4) + (packetSize % 4 > 0 ? 1 : 0));
        packet[2] = (byte)(hdrSeq >> 8);
        packet[3] = (byte)hdrSeq;

        // RTP header (12 B)
        packet[4] = 0x80;
        packet[5] = 0x60; // PT=96, marker=0
        packet[6] = (byte)(seq >> 8);
        packet[7] = (byte)seq;
        packet[8] = (byte)(timestamp >> 24);
        packet[9] = (byte)(timestamp >> 16);
        packet[10] = (byte)(timestamp >> 8);
        packet[11] = (byte)timestamp;
        packet[12] = (byte)(ssrc >> 24);
        packet[13] = (byte)(ssrc >> 16);
        packet[14] = (byte)(ssrc >> 8);
        packet[15] = (byte)ssrc;

        // ALAC payload → CBC (od offsetu 16; padding None — reszta kopiowana jawnie)
        using var aes = Aes.Create();
        aes.Key = FixedKey;
        aes.IV = FixedIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        int payloadLen = alacPayload.Length;
        int blocks = payloadLen / 16;
        using var encryptor = aes.CreateEncryptor();
        int srcOffset = 0;
        for (int i = 0; i < blocks; i++)
        {
            encryptor.TransformBlock(alacPayload.Slice(srcOffset, 16).ToArray(), 0, 16, packet, 16 + srcOffset);
            srcOffset += 16;
        }
        // Ogon (< 16 B) — kopiowany jawnie (jak TuneBlade Buffer.BlockCopy)
        alacPayload.Slice(srcOffset).CopyTo(packet.AsSpan(16 + srcOffset));

        return packet;
    }
}
