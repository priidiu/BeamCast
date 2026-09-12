using System.Security.Cryptography;
using AirNext.Core.Raop;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>AES encryption tests (RHI-144) — faithful TuneBlade port.</summary>
public class AesEncryptionTests
{
    [Fact]
    public void FixedKeys_AreTuneBladeValues()
    {
        // Z dekompilacji TuneBlade initFixedEncryptionKeys()
        Assert.Equal(new byte[] { 11, 160, 214, 176, 146, 6, 210, 20, 248, 213, 154, 240, 159, 171, 187, 21 }, AesEncryption.FixedKey);
        Assert.Equal(new byte[] { 48, 124, 13, 0, 96, 67, 200, 7, 244, 245, 203, 81, 65, 45, 168, 194 }, AesEncryption.FixedIv);
        Assert.StartsWith("1phM", AesEncryption.RsaaesKey);
        Assert.Equal(Convert.ToBase64String(AesEncryption.FixedIv), AesEncryption.IvBase64);
    }

    [Fact]
    public void EncryptAudioPacket_HasPreHeader_RtpHeader_AndCbcPayload()
    {
 var alac = new byte[32]; // 2 full bloki CBC
        new Random(5).NextBytes(alac);

        var packet = AesEncryption.EncryptAudioPacket(seq: 1234, timestamp: 5000, ssrc: 0x34249563, alac);

        Assert.Equal(4 + 12 + alac.Length, packet.Length);

        // Pre-header (4 B): 0x80 0xD6 (PT=86, M=1) + seq = packetSize/4
        Assert.Equal(0x80, packet[0]);
        Assert.Equal((byte)(0x80 | RtpPacket.PayloadTypeResendReply), packet[1]);
        int packetSize = alac.Length + 12;
        ushort hdrSeq = (ushort)((packetSize / 4) + (packetSize % 4 > 0 ? 1 : 0));
        Assert.Equal((byte)(hdrSeq >> 8), packet[2]);
        Assert.Equal((byte)hdrSeq, packet[3]);

        // RTP header (12 B): PT=96, seq, ts, ssrc
        Assert.Equal(0x80, packet[4]);
        Assert.Equal(0x60, packet[5]);
        Assert.Equal(0x04, packet[6]); // seq 1234 = 0x04D2
        Assert.Equal(0xD2, packet[7]);
        Assert.Equal(0x00, packet[8]); // ts 5000
        Assert.Equal(0x00, packet[9]);
        Assert.Equal(0x13, packet[10]);
        Assert.Equal(0x88, packet[11]);
        Assert.Equal(0x34, packet[12]); // ssrc
        Assert.Equal(0x24, packet[13]);
        Assert.Equal(0x95, packet[14]);
        Assert.Equal(0x63, packet[15]);

 // Payload CBC — pierwszy blok NIE may be jawny (zaszyfrowany)
        Assert.NotEqual(alac.AsSpan(0, 16).ToArray(), packet.AsSpan(16, 16).ToArray());
    }

    [Fact]
    public void EncryptDecrypt_Roundtrip_WithSameKeys()
    {
        var alac = new byte[48];
        new Random(9).NextBytes(alac);
        var packet = AesEncryption.EncryptAudioPacket(1, 100, 0x11111111, alac);

        // Odszyfruj payload CBC (od offsetu 16) tymi samymi kluczami
        using var aes = Aes.Create();
        aes.Key = AesEncryption.FixedKey;
        aes.IV = AesEncryption.FixedIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        var encrypted = packet.AsSpan(16).ToArray();
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        Assert.Equal(alac, decrypted);
    }

    [Fact]
    public void EncryptAudioPacket_OddLength_TailCopiedPlain()
    {
        // TuneBlade: tail < 16 B copied in the clear (Buffer.BlockCopy) — NOT encrypted
        var alac = new byte[20]; // 16 + 4
        new Random(3).NextBytes(alac);
        var packet = AesEncryption.EncryptAudioPacket(1, 100, 1, alac);

        Assert.Equal(4 + 12 + 20, packet.Length);
        // Ostatnie 4 bajty (ogon) = jawny ALAC
        Assert.Equal(alac.AsSpan(16).ToArray(), packet.AsSpan(16 + 16, 4).ToArray());
 // Pierwsze 16 bytes zaszyfrowane
        Assert.NotEqual(alac.AsSpan(0, 16).ToArray(), packet.AsSpan(16, 16).ToArray());
    }
}
