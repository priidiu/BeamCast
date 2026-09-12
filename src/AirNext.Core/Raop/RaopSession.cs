using System.Net;
using System.Net.Sockets;

namespace AirNext.Core.Raop;

/// <summary>
/// RAOP (AirPlay 1) session to a single receiver — flow verified live
/// przechwytem TuneBlade→WiiM Mini (docs/wireshark-analysis.md):
///   POST /auth-setup → OPTIONS → ANNOUNCE → SETUP → RECORD → (SET_PARAMETER...) → TEARDOWN
///
/// Supports:
///  - auth-setup (Device Verification) — wymagane przez WiiM Mini i Apple TV
///  - plaintext ALAC (no encryption — WiiM accepts et=0)
/// - brak header Audio-Latency w odpowiedzi RECORD (WiiM go nie zwraca)
/// </summary>
public sealed class RaopSession : IDisposable, IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly string _host;
    private readonly int _rtspPort;
    private int _cseq;

    public string SessionId { get; private set; } = string.Empty;
    public long? AudioLatencySamples { get; private set; }
    public ushort ServerPort { get; private set; }
    public ushort ControlPort { get; private set; }
    public ushort TimingPort { get; private set; }

    public const ushort LocalControlPort = 6002;
    public const ushort LocalTimingPort = 6003;

    private RaopSession(TcpClient tcp, string host, int rtspPort)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _host = host;
        _rtspPort = rtspPort;
    }

    public static async Task<RaopSession> ConnectAsync(string host, int rtspPort, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, rtspPort, ct).ConfigureAwait(false);
        return new RaopSession(tcp, host, rtspPort);
    }

    /// <summary>
    /// POST /auth-setup — Device Verification. REQUIRED by WiiM Mini (without it the session
 /// przechodzi, ale nie ma audio!).
 /// Payload: FIXED 33 bajty — exactly jak TuneBlade (z dekompilacji TuneBlade.exe,
    /// sendAuthSetupRequest: new byte[33]{1,78,234,208,...}).
    /// </summary>
    public async Task AuthSetupAsync(CancellationToken ct = default)
    {
        byte[] requestPayload = new byte[33]
        {
            1, 78, 234, 208, 78, 169, 46, 71, 105, 210,
            225, 251, 208, 150, 129, 213, 148, 168, 239, 24,
            69, 74, 36, 174, 175, 179, 20, 151, 13, 160,
            181, 163, 73
        };
        var req = RtspMessage.Request(
            "POST", "/auth-setup",
            ("CSeq", NextCSeq().ToString()),
            ("User-Agent", "AirNext/0.1 (Windows)"),
            ("Client-Instance", ClientInstance),
            ("DACP-ID", ClientInstance),
            ("Active-Remote", "1012014789"),
            ("Content-Type", "application/octet-stream"),
            ("Content-Length", requestPayload.Length.ToString()));
        req.Body = requestPayload;

        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != 200)
            throw new InvalidOperationException($"auth-setup nieudany: HTTP {resp.StatusCode} {resp.StatusText}");
    }

    public async Task OptionsAsync(CancellationToken ct = default)
    {
        var req = RtspMessage.Request("OPTIONS", "*", BaseHeaders());
        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != 200)
            throw new InvalidOperationException($"OPTIONS nieudany: HTTP {resp.StatusCode}");
    }

    /// <summary>ANNOUNCE z SDP — ALAC 16/44.1, plaintext (et=0), bez min-latency (jak TuneBlade).</summary>
    public async Task AnnounceAsync(string sessionPath, int formatCookieMaxFrames = 352, bool encrypt = false, CancellationToken ct = default)
    {
        var sdp = BuildSdp(_host, sessionPath, formatCookieMaxFrames, encrypt);
        var req = RtspMessage.Request(
            "ANNOUNCE", $"rtsp://{_host}:{_rtspPort}/{sessionPath}",
            ("CSeq", NextCSeq().ToString()),
            ("Content-Type", "application/sdp"),
            ("Content-Length", sdp.Length.ToString()),
            ("User-Agent", "AirNext/0.1 (Windows)"));
        req.Body = System.Text.Encoding.UTF8.GetBytes(sdp);

        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != 200)
            throw new InvalidOperationException($"ANNOUNCE nieudany: HTTP {resp.StatusCode}");
    }

 /// <summary>SETUP — 3 channels UDP (audio/control/timing). Porty lokalne dynamiczne (sender).</summary>
    public async Task SetupAsync(string sessionPath, int localControlPort = LocalControlPort, int localTimingPort = LocalTimingPort, CancellationToken ct = default)
    {
        var req = RtspMessage.Request(
            "SETUP", $"rtsp://{_host}:{_rtspPort}/{sessionPath}",
            ("CSeq", NextCSeq().ToString()),
            ("Transport", $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={localControlPort};timing_port={localTimingPort}"),
            ("User-Agent", "AirNext/0.1 (Windows)"));

        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != 200)
            throw new InvalidOperationException($"SETUP nieudany: HTTP {resp.StatusCode}");

        ParseTransport(resp.Transport);
    }

 /// <summary>RECORD — start streamu; Audio-Latency may be nieobecna (WiiM jej nie zwraca).</summary>
    public async Task RecordAsync(string sessionPath, ushort seq, uint rtptime, CancellationToken ct = default)
    {
        var req = RtspMessage.Request(
            "RECORD", $"rtsp://{_host}:{_rtspPort}/{sessionPath}",
            ("CSeq", NextCSeq().ToString()),
            ("Session", SessionId),
            ("Range", "npt=0-"),
            ("RTP-Info", $"seq={seq};rtptime={rtptime}"),
            ("User-Agent", "AirNext/0.1 (Windows)"));

        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != 200)
            throw new InvalidOperationException($"RECORD nieudany: HTTP {resp.StatusCode}");

 AudioLatencySamples = resp.AudioLatencySamples; // may be null — WiiM nie zwraca
        if (!string.IsNullOrEmpty(resp.Headers.GetValueOrDefault("Session")))
            SessionId = resp.Headers["Session"];

 // Remember startowe seq/rtptime (przydatne po FLUSH do wznowienia)
        _lastFlushedSeq = seq;
        _lastFlushedRtptime = rtptime;
    }

    /// <summary>
    /// FLUSH (RHI-146) — reset receiver buffer (pause/resume, track change).
    /// W AirPlay 1: prosta komenda RTSP z CSeq + Session → 200 OK.
 /// Optionally includes RTP-Info (new start seq/rtptime) — the receiver may return it.
    /// </summary>
    public async Task FlushAsync(string sessionPath, CancellationToken ct = default)
    {
        var req = RtspMessage.Request(
            "FLUSH", $"rtsp://{_host}:{_rtspPort}/{sessionPath}",
            ("CSeq", NextCSeq().ToString()),
            ("Session", SessionId),
            ("User-Agent", "AirNext/0.1 (Windows)"));

        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != 200)
            throw new InvalidOperationException($"FLUSH nieudany: HTTP {resp.StatusCode}");

 // Receiver may return new RTP-Info — useful on resume
        var rtpInfo = resp.Headers.GetValueOrDefault("RTP-Info");
        if (!string.IsNullOrEmpty(rtpInfo))
            ParseRtpInfo(rtpInfo);
    }

    private void ParseRtpInfo(string rtpInfo)
    {
        var parts = rtpInfo.Split(';');
        foreach (var part in parts)
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length != 2)
                continue;
            if (kv[0].Equals("seq", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(kv[1], out var seq))
                _lastFlushedSeq = seq;
            else if (kv[0].Equals("rtptime", StringComparison.OrdinalIgnoreCase) && uint.TryParse(kv[1], out var rtptime))
                _lastFlushedRtptime = rtptime;
        }
    }

    /// <summary>Seq z ostatniego RTP-Info (RECORD lub FLUSH) — do wznowienia.</summary>
    public ushort? LastFlushedSeq => _lastFlushedSeq;
    /// <summary>Rtptime z ostatniego RTP-Info (RECORD lub FLUSH) — do wznowienia.</summary>
    public uint? LastFlushedRtptime => _lastFlushedRtptime;

    private ushort? _lastFlushedSeq;
    private uint? _lastFlushedRtptime;

    /// <summary>
    /// SET_PARAMETER — np. volume (jak TuneBlade po RECORD: "volume: -0.001000").
    /// </summary>
    public async Task SetParameterAsync(string sessionPath, string body, string contentType = "text/parameters", CancellationToken ct = default)
    {
        await SetParameterAsync(sessionPath, System.Text.Encoding.UTF8.GetBytes(body), contentType, rtpInfo: null, ct).ConfigureAwait(false);
    }

    /// <summary>SET_PARAMETER z binarnym body (np. metadata DMAP).</summary>
    public async Task SetParameterAsync(string sessionPath, byte[] body, string contentType, string? rtpInfo = null, CancellationToken ct = default)
    {
        var headers = new List<(string, string)>
        {
            ("CSeq", NextCSeq().ToString()),
            ("Session", SessionId),
            ("Content-Type", contentType),
            ("Content-Length", body.Length.ToString()),
            ("User-Agent", "AirNext/0.1 (Windows)"),
        };
        if (!string.IsNullOrEmpty(rtpInfo))
 headers.Insert(1, ("RTP-Info", rtpInfo)); // jak TuneBlade: RTP-Info przed the rest

        var req = RtspMessage.Request(
            "SET_PARAMETER", $"rtsp://{_host}:{_rtspPort}/{sessionPath}",
            headers.ToArray());
        req.Body = body;

        var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != 200)
            throw new InvalidOperationException($"SET_PARAMETER nieudany: HTTP {resp.StatusCode}");
    }

    public async Task TeardownAsync(string sessionPath, CancellationToken ct = default)
    {
        try
        {
            var req = RtspMessage.Request(
                "TEARDOWN", $"rtsp://{_host}:{_rtspPort}/{sessionPath}",
                ("CSeq", NextCSeq().ToString()),
                ("Session", SessionId),
                ("User-Agent", "AirNext/0.1 (Windows)"));
            await SendAsync(req, ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Mierzy TCP RTT przez OPTIONS request (RHI-172 fallback gdy NTP timing client
 /// does not get a reply from the receiver). Returns RTT in ms or -1 on error.
    /// </summary>
    public async Task<double> MeasureRttAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await OptionsAsync(ct).ConfigureAwait(false);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    private async Task<RtspMessage> SendAsync(RtspMessage req, CancellationToken ct)
    {
        var bytes = req.Serialize();
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);

 // Czytamy response (max 64 KB — certyfikat auth-setup bywa large)
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
 // Najpierw check, czy mamy already full headers w buforze
            var current = ms.ToArray();
            int headerEnd = FindHeaderEnd(current);
            if (headerEnd >= 0)
            {
                var partial = RtspMessage.Parse(current);
                int bodyLen = partial.Body.Length;
                int expectedTotal = headerEnd + 4 + bodyLen;
                if (current.Length >= expectedTotal)
                    return RtspMessage.Parse(current.AsSpan(0, expectedTotal));
            }

            int n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("RTSP connection closed before response.");
            ms.Write(buffer, 0, n);
            if (ms.Length > 64 * 1024)
                throw new InvalidOperationException("RTSP response exceeds 64 KB.");
        }
    }

    private static int FindHeaderEnd(byte[] data)
    {
        for (int i = 0; i < data.Length - 3; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private void ParseTransport(string? transport)
    {
        if (transport is null)
            throw new InvalidOperationException("Missing Transport header in SETUP response.");

        ServerPort = ParsePort(transport, "server_port");
        ControlPort = ParsePort(transport, "control_port");
        TimingPort = ParsePort(transport, "timing_port");
    }

    private static ushort ParsePort(string transport, string key)
    {
        var parts = transport.Split(';');
        foreach (var part in parts)
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return ushort.Parse(kv[1]);
        }
        throw new InvalidOperationException($"Missing {key} in Transport header.");
    }

    private static string BuildSdp(string receiverAddress, string sessionPath, int maxFrames, bool encrypt = false)
    {
        // fmtp: 352 0 16 40 10 14 2 255 0 0 44100 — magic cookie ALAC 16/44.1 stereo (z przechwytu)
 // o=/c= point at the RECEIVER address (TuneBlade: c=IN IP4 192.168.1.13) — not 0.0.0.0
        // encrypt: a=rsaaeskey + a=aesiv (RHI-144) — jak TuneBlade EncryptionNeeded
        var sdp =
            "v=0\r\n" +
            $"o=iTunes {sessionPath} 0 IN IP4 {receiverAddress}\r\n" +
            "s=iTunes\r\n" +
            $"c=IN IP4 {receiverAddress}\r\n" +
            "t=0 0\r\n" +
            "m=audio 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 AppleLossless\r\n" +
            $"a=fmtp:96 {maxFrames} 0 16 40 10 14 2 255 0 0 44100\r\n";
        if (encrypt)
            sdp += $"a=rsaaeskey:{AesEncryption.RsaaesKey}\r\na=aesiv:{AesEncryption.IvBase64}\r\n";
        return sdp;
    }

    private (string Name, string Value)[] BaseHeaders() => new[]
    {
        ("CSeq", NextCSeq().ToString()),
        ("User-Agent", "AirNext/0.1 (Windows)"),
        ("Client-Instance", ClientInstance),
        ("DACP-ID", ClientInstance),
        ("Active-Remote", "1012014789"),
    };

    private int NextCSeq() => ++_cseq;

 private static string ClientInstance => "56B6CB929BB20486"; // constant na razie (jak TuneBlade)

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
