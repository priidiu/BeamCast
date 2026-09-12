using System.Text;
using AirNext.Core.Discovery;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>Testy parsera mDNS/DNS-SD (RHI-160) — kompresja QNAME, TXT, PTR/SRV.</summary>
public class MdnsParserTests
{
    [Fact]
    public void ReadName_Plain_NoCompression()
    {
        // "abc.def" + zero terminator
        byte[] buf = { 3, (byte)'a', (byte)'b', (byte)'c', 3, (byte)'d', (byte)'e', (byte)'f', 0 };
        int pos = 0;
        var name = MdnsParser.ReadName(buf, ref pos);
        Assert.Equal("abc.def", name);
        Assert.Equal(9, pos);
    }

    [Fact]
    public void ReadName_WithCompressionPointer()
    {
        // Packet: [0..6] = name "x.local" (1, 'x', 5, 'l','o','c','a','l', 0), [7..] = pointer do offsetu 0
        byte[] buf =
        {
            1, (byte)'x', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0,
            // pointer: 0xC0 | 0x00 → offset 0
            0xC0, 0x00,
        };
        int pos = 9;
        var name = MdnsParser.ReadName(buf, ref pos);
        Assert.Equal("x.local", name);
        // po skoku pos wskazuje za pointer
        Assert.Equal(11, pos);
    }

    [Fact]
    public void ParseTxt_KeyValues()
    {
 // "tp=0x44" + "am=WiiM Mini" + "cn" (bez values)
        byte[] txt =
        {
            7, (byte)'t', (byte)'p', (byte)'=', (byte)'0', (byte)'x', (byte)'4', (byte)'4',
            12, (byte)'a', (byte)'m', (byte)'=', (byte)'W', (byte)'i', (byte)'i', (byte)'M',
            (byte)' ', (byte)'M', (byte)'i', (byte)'n', (byte)'i',
            2, (byte)'c', (byte)'n',
        };
        var dict = MdnsParser.ParseTxt(txt);
        Assert.Equal("0x44", dict["tp"]);
        Assert.Equal("WiiM Mini", dict["am"]);
        Assert.Equal("", dict["cn"]);
    }

    [Fact]
    public void BuildPtrQuery_Structure()
    {
        var q = MdnsParser.BuildPtrQuery("_airplay._tcp.local");
        // header(12) + [1+8 "_airplay"] + [1+4 "_tcp"] + [1+5 "local"] + 0 + type(2) + class(2)
        Assert.Equal(12 + 9 + 5 + 6 + 1 + 2 + 2, q.Length);
        Assert.Equal(0x00, q[2]); // flags query
        Assert.Equal(0x00, q[3]);
        Assert.Equal(0x00, q[4]); // QDCOUNT hi
        Assert.Equal(0x01, q[5]); // QDCOUNT lo = 1
        // name "_airplay._tcp.local" z offsetu 12
        int p = 12;
        Assert.Equal(8, q[p]); p++;
        Assert.Equal("_airplay", Encoding.UTF8.GetString(q, p, 8)); p += 8;
        Assert.Equal(4, q[p]); p++;
        Assert.Equal("_tcp", Encoding.UTF8.GetString(q, p, 4)); p += 4;
        Assert.Equal(5, q[p]); p++;
        Assert.Equal("local", Encoding.UTF8.GetString(q, p, 5)); p += 5;
        Assert.Equal(0, q[p]); p++;
        Assert.Equal(MdnsParser.TypePtr >> 8, q[p]); p++;
        Assert.Equal(MdnsParser.TypePtr & 0xFF, q[p]);
    }

    [Fact]
    public void Parse_FullMessage_WithPtrAnswer()
    {
 // Zbuduj response: header (response, 1 answer) + question PTR + answer PTR
 // Answer: name=pointer do pytania (0xC00C), type=PTR, class=IN, ttl, rdlen, rdata="WiiM._airplay._tcp.local" z compression
        var name = "_airplay._tcp.local";
        var nameBytes = EncodeName(name);
        var instanceBytes = EncodeName("WiiM Mini._airplay._tcp.local");

        using var ms = new MemoryStream();
        // header
        WriteU16(ms, 0x0000); // ID
        WriteU16(ms, 0x8400); // flags: response + AA
        WriteU16(ms, 1); // QD
        WriteU16(ms, 1); // AN
        WriteU16(ms, 0); // NS
        WriteU16(ms, 0); // AR
        // question
        ms.Write(nameBytes);
        WriteU16(ms, MdnsParser.TypePtr);
        WriteU16(ms, MdnsParser.QClassIn);
        // answer: name pointer do pytania (offset 12)
        ms.WriteByte(0xC0); ms.WriteByte(12);
        WriteU16(ms, MdnsParser.TypePtr);
        WriteU16(ms, MdnsParser.QClassIn);
        WriteU32(ms, 4500); // TTL
        WriteU16(ms, (ushort)instanceBytes.Length); // rdlen
 ms.Write(instanceBytes); // rdata (bez kompresji inside)

        var msg = MdnsParser.Parse(ms.ToArray());
        Assert.True(msg.Response);
        Assert.Single(msg.Questions);
        Assert.Single(msg.Answers);
        Assert.Equal(MdnsParser.TypePtr, msg.Answers[0].Type);

 // Decode PTR target z offsetu (kompresja may be — tu plain)
        var target = MdnsParser.DecodeNameAtOffset(ms.ToArray(), msg.Answers[0].DataOffset);
        Assert.Equal("WiiM Mini._airplay._tcp.local", target);
    }

    private static byte[] EncodeName(string name)
    {
        using var ms = new MemoryStream();
        foreach (var label in name.Split('.'))
        {
            ms.WriteByte((byte)label.Length);
            ms.Write(Encoding.UTF8.GetBytes(label));
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static void WriteU16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
}
