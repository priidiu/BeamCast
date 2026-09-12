using System.Text;

namespace AirNext.Core.Discovery;

/// <summary>
/// Minimalny parser DNS/mDNS (RHI-160) — zero dependencies, tylko to co potrzebne
/// do discovery AirPlay: PTR, SRV, TXT, A/AAAA. Supports compression QNAME (0xC0).
/// </summary>
public static class MdnsParser
{
    public const ushort QClassIn = 1;
    public const ushort QClassUnicastResponse = 0x8000; // mDNS QU bit

    public static ushort TypePtr = 12;
    public static ushort TypeTxt = 16;
    public static ushort TypeSrv = 33;
    public static ushort TypeA = 1;
    public static ushort TypeAaaa = 28;

    public sealed record Message(
        ushort Id, bool Response, ushort Flags,
        List<Question> Questions, List<Record> Answers);

    public sealed record Question(string Name, ushort Type, ushort Class);

    public sealed record Record(string Name, ushort Type, ushort Class, uint Ttl, byte[] Data, int DataOffset);

 /// <summary>Parsuje message DNS z bufora.</summary>
    public static Message Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 12)
            throw new ArgumentException("DNS header too short");

        ushort id = ReadU16(buf, 0);
        ushort flags = ReadU16(buf, 2);
        bool response = (flags & 0x8000) != 0;
        int qd = ReadU16(buf, 4);
        int an = ReadU16(buf, 6);
        int ns = ReadU16(buf, 8);
        int ar = ReadU16(buf, 10);

        int pos = 12;
        var questions = new List<Question>(qd);
        for (int i = 0; i < qd && pos < buf.Length; i++)
        {
            var name = ReadName(buf, ref pos);
            if (name is null) break;
            ushort type = ReadU16(buf, pos); pos += 2;
            ushort cls = ReadU16(buf, pos); pos += 2;
            questions.Add(new Question(name, type, cls));
        }

        int total = an + ns + ar;
        var answers = new List<Record>(total);
        for (int i = 0; i < total && pos < buf.Length; i++)
        {
            var name = ReadName(buf, ref pos);
            if (name is null) break;
            ushort type = ReadU16(buf, pos); pos += 2;
            ushort cls = ReadU16(buf, pos); pos += 2;
            uint ttl = ReadU32(buf, pos); pos += 4;
            ushort rdlen = ReadU16(buf, pos); pos += 2;
            if (pos + rdlen > buf.Length) break;
            int dataOffset = pos;
            var data = buf.Slice(pos, rdlen).ToArray();
            pos += rdlen;
            answers.Add(new Record(name, type, cls, ttl, data, dataOffset));
        }

        return new Message(id, response, flags, questions, answers);
    }

 /// <summary>Build a DNS-SD query packet: PTR for a service (e.g. _airplay._tcp.local).</summary>
    public static byte[] BuildPtrQuery(string serviceName, ushort id = 0)
    {
        // PTR _airplay._tcp.local — pytanie o PTR, klasa IN, QU bit (unicast response)
        using var ms = new MemoryStream();
        WriteU16(ms, id);
        WriteU16(ms, 0); // flags = query
        WriteU16(ms, 1); // QDCOUNT
        WriteU16(ms, 0); // ANCOUNT
        WriteU16(ms, 0); // NSCOUNT
        WriteU16(ms, 0); // ARCOUNT
        WriteName(ms, serviceName);
        WriteU16(ms, TypePtr);
        WriteU16(ms, QClassIn | QClassUnicastResponse);
        return ms.ToArray();
    }

    /// <summary>Buduje zapytanie A (lub SRV/TXT) dla nazwy.</summary>
    public static byte[] BuildQuery(string name, ushort type, ushort id = 0)
    {
        using var ms = new MemoryStream();
        WriteU16(ms, id);
        WriteU16(ms, 0);
        WriteU16(ms, 1);
        WriteU16(ms, 0);
        WriteU16(ms, 0);
        WriteU16(ms, 0);
        WriteName(ms, name);
        WriteU16(ms, type);
        WriteU16(ms, QClassIn | QClassUnicastResponse);
        return ms.ToArray();
    }

 /// <summary>Czyta name DNS z compression (0xC0 + offset).</summary>
    public static string? ReadName(ReadOnlySpan<byte> buf, ref int pos)
    {
        var sb = new StringBuilder();
        int p = pos;
        int jumps = 0;
        bool jumped = false;
        while (true)
        {
            if (p >= buf.Length) return null;
            byte len = buf[p];
            if (len == 0)
            {
                p++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= buf.Length) return null;
                int offset = ((len & 0x3F) << 8) | buf[p + 1];
                if (!jumped)
                {
                    pos = p + 2; // po skoku kontynuujemy od offsetu
                    jumped = true;
                }
                if (jumps++ > 20) return null; // ochrona przed cyklami
                p = offset;
                continue;
            }
            if (p + 1 + len > buf.Length) return null;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(buf.Slice(p + 1, len)));
            p += 1 + len;
        }
        if (!jumped)
            pos = p;
        return sb.ToString();
    }

    /// <summary>Parsuje TXT record: lista "key=value" (length-prefixed).</summary>
    public static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        while (pos < data.Length)
        {
            int len = data[pos++];
            if (len == 0 || pos + len > data.Length) continue;
            var entry = Encoding.UTF8.GetString(data.Slice(pos, len));
            pos += len;
            int eq = entry.IndexOf('=');
            if (eq >= 0)
                result[entry[..eq]] = entry[(eq + 1)..];
            else
                result[entry] = "";
        }
        return result;
    }

 /// <summary>Parsuje SRV record: priority(2) weight(2) port(2) target(name z compression).</summary>
    public static (ushort Priority, ushort Weight, ushort Port, string Target) ParseSrv(
        ReadOnlySpan<byte> data, ReadOnlySpan<byte> fullMessage)
    {
        int pos = 0;
        ushort priority = ReadU16(data, pos); pos += 2;
        ushort weight = ReadU16(data, pos); pos += 2;
        ushort port = ReadU16(data, pos); pos += 2;
 // SRV target is a DNS name — may use compression relative to the full packet.
 // If rdata has compression (0xC0), we must read from the full packet. In practice
 // mDNS answers with compression → use the full packet via offset.
        return (priority, weight, port, Encoding.UTF8.GetString(data.Slice(pos)));
    }

 /// <summary>Decode a name from rdata (PTR/SRV target) with compression vs the full packet.</summary>
    public static string DecodeNameAtOffset(ReadOnlySpan<byte> fullPacket, int dataOffset)
    {
        int p = dataOffset;
        return ReadName(fullPacket, ref p) ?? "";
    }

    public static ushort ReadU16(ReadOnlySpan<byte> b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
    public static uint ReadU32(ReadOnlySpan<byte> b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static void WriteU16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    private static void WriteName(Stream s, string name)
    {
        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0) continue;
            if (label.Length > 63) throw new ArgumentException("DNS label too long");
            s.WriteByte((byte)label.Length);
            s.Write(Encoding.UTF8.GetBytes(label));
        }
        s.WriteByte(0);
    }
}
