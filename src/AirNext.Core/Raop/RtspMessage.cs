using System.Text;

namespace AirNext.Core.Raop;

/// <summary>
/// Prosta reprezentacja message RTSP (request i response) — enough dla RAOP.
/// Zgodna z obserwacjami z przechwytu TuneBlade→WiiM Mini (docs/wireshark-analysis.md).
/// </summary>
public sealed class RtspMessage
{
    public string RequestLine { get; set; } = string.Empty;
    public int StatusCode { get; private set; }
    public string StatusText { get; private set; } = string.Empty;
    public bool IsResponse => StatusCode > 0;
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string BodyText => Encoding.UTF8.GetString(Body);

    public static RtspMessage Parse(ReadOnlySpan<byte> data)
    {
 // Szukamy end headers (CRLF CRLF)
        int headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
            throw new FormatException("Incomplete RTSP message (no end of headers).");

        var head = Encoding.ASCII.GetString(data[..headerEnd]);
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        var msg = new RtspMessage();
        var first = lines[0];

        if (first.StartsWith("RTSP/", StringComparison.Ordinal))
        {
 // Response: RTSP/1.0 200 OK
            var parts = first.Split(' ', 3);
            msg.StatusCode = int.Parse(parts[1]);
            msg.StatusText = parts.Length > 2 ? parts[2] : string.Empty;
        }
        else
        {
 // Request: METHOD uri RTSP/1.0
            msg.RequestLine = first;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0)
                continue;
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            msg.Headers[name] = value;
        }

 // Body: po CRLF CRLF; Content-Length decyduje o realnej length
        int bodyStart = headerEnd + 4;
        if (msg.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len))
        {
            int available = data.Length - bodyStart;
            msg.Body = data.Slice(bodyStart, Math.Min(len, available)).ToArray();
        }
        else
        {
            msg.Body = data[bodyStart..].ToArray();
        }

        return msg;
    }

    public static RtspMessage Request(string method, string uri, params (string Name, string Value)[] headers)
    {
        var msg = new RtspMessage { RequestLine = $"{method} {uri} RTSP/1.0" };
        foreach (var (name, value) in headers)
            msg.Headers[name] = value;
        return msg;
    }

    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(RequestLine).Append("\r\n");
        foreach (var (name, value) in Headers)
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        if (Body.Length > 0 && !Headers.ContainsKey("Content-Length"))
            sb.Append("Content-Length: ").Append(Body.Length).Append("\r\n");
        sb.Append("\r\n");

        using var ms = new MemoryStream();
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        ms.Write(head);
        ms.Write(Body);
        return ms.ToArray();
    }

 /// <summary>Header Audio-Latency (samples) — may be nieobecny (WiiM go nie zwraca).</summary>
    public long? AudioLatencySamples
    {
        get
        {
            if (Headers.TryGetValue("Audio-Latency", out var v) && long.TryParse(v, out var samples))
                return samples;
            return null;
        }
    }

 /// <summary>Header Transport z odpowiedzi SETUP (server/control/timing ports).</summary>
    public string? Transport => Headers.TryGetValue("Transport", out var v) ? v : null;
}
