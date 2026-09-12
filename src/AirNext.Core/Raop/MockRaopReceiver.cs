using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirNext.Core.Raop;

/// <summary>
/// Headless RAOP receiver mock (RHI-138) — localhost RTSP server, behaving 
/// jak WiiM Mini na podstawie przechwytu (docs/wireshark-analysis.md):
///  - Server: AirTunes/366.0
///  - POST /auth-setup → 200 OK (certyfikat symulowany)
/// - RECORD → 200 OK BEZ header Audio-Latency (jak WiiM)
///  - SETUP → przydziela porty UDP
///
/// Do tests CI i rozwoju bez hardware (NFR-10).
/// </summary>
public sealed class MockRaopReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private Task? _rtpLoop;
    private readonly List<string> _log = new();
    private readonly object _lock = new();
    private UdpClient? _rtpListener;
 private UdpClient? _controlListener; // listen for 86 on the receiver control_port
    private int _rtpPacketCount;
    private readonly object _rtpLock = new();
    private readonly List<byte[]> _rtpPayloads = new();
    private readonly List<int> _rtpSequences = new();

 /// <summary>Probability celowego gubienia packets audio (0..1) — do tests retransmisji (RHI-143).</summary>
    public double DropFraction { get; init; }

 /// <summary>Sender control port (destination of type-85 requests).</summary>
    private int _senderControlPort;

 /// <summary>Packets recovered via retransmission (86) — diagnostics.</summary>
    public int ResendRecoveredCount { get; private set; }

    /// <summary>All packets received on control_port (including non-86) — diagnostics.</summary>
    public int ControlPacketsReceived { get; private set; }

    public int Port { get; }
    public IReadOnlyList<string> Log => _log;

 /// <summary>Liczba odebranych packets RTP (payload 96) na server_port.</summary>
    public int RtpPacketCount
    {
        get { lock (_rtpLock) return _rtpPacketCount; }
    }

 /// <summary>Payloaded RTP (payload 96) w order — do tests ABX/dekodowania ALAC.</summary>
    public IReadOnlyList<byte[]> RtpPayloads
    {
        get { lock (_rtpLock) return _rtpPayloads.ToArray(); }
    }

 /// <summary>Skonfigurowana value Audio-Latency — null = WiiM nie zwraca.</summary>
    public long? AudioLatencySamples { get; init; }

    private MockRaopReceiver(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Losowe porty RTP (jak prawdziwy WiiM) — TRZY OSOBNE sockety (inaczej ten sam port!)
        using (var probe1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            ServerPort = ((IPEndPoint)probe1.Client.LocalEndPoint!).Port;
        using (var probe2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            ControlPort = ((IPEndPoint)probe2.Client.LocalEndPoint!).Port;
        using (var probe3 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            TimingPort = ((IPEndPoint)probe3.Client.LocalEndPoint!).Port;

 // ControlPort must actually be bound — the sender replies 86 to our 85 here
        _controlListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, ControlPort));
        _controlListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _controlLoop = Task.Run(() => ControlListenAsync(), _cts.Token);
    }

    private Task? _controlLoop;

    private async Task ControlListenAsync()
    {
        var buffer = new byte[2048];
        while (!_cts.IsCancellationRequested && _controlListener is not null)
        {
            try
            {
                var result = await _controlListener.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                ControlPacketsReceived++;
 // Response 86 ma pre-header 4 B + RTP — PT na offsecie 4+1=5 (nie 1!)
                int type = result.Buffer.Length >= 6 ? result.Buffer[5] & 0x7F : -1;
                if (type == RtpPacket.PayloadTypeResendReply && result.Buffer.Length >= 4 + 12)
                {
 // Response 86: pre-header 4 B + RTP audio (seq na offset 4+2)
                    ushort seq = (ushort)((result.Buffer[4 + 2] << 8) | result.Buffer[4 + 3]);
                    lock (_rtpLock)
                    {
                        _rtpPacketCount++;
                        _rtpPayloads.Add(result.Buffer.AsSpan(16).ToArray()); // 4 pre-header + 12 RTP header
                        ResendRecoveredCount++;
                        _rtpSequences.Add(seq);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }
    }

    /// <summary>Port audio (server_port) przydzielony przez mock — jak prawdziwy WiiM.</summary>
    public int ServerPort { get; }
    public int ControlPort { get; }
    public int TimingPort { get; }

    public static MockRaopReceiver Start(long? audioLatencySamples = null, double dropFraction = 0.0)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var receiver = new MockRaopReceiver(listener) { AudioLatencySamples = audioLatencySamples, DropFraction = dropFraction };
        receiver._loop = receiver.RunAsync();
        return receiver;
    }

 /// <summary>Start listen RTP na podanym porcie (server_port z SETUP).</summary>
    public void StartRtpListener(int serverPort)
    {
        _rtpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, serverPort));
        _rtpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _rtpLoop = Task.Run(() => RtpListenAsync(), _cts.Token);
    }

 /// <summary>Set the sender control port (target of 85) — called from tests after Connect().</summary>
    public void SetSenderControlPort(int senderControlPort) => _senderControlPort = senderControlPort;

    private async Task RtpListenAsync()
    {
        var buffer = new byte[2048];
        var rng = new Random(42);
        while (!_cts.IsCancellationRequested && _rtpListener is not null)
        {
            try
            {
                var result = await _rtpListener.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                int type = result.Buffer.Length >= 2 ? result.Buffer[1] & 0x7F : -1;

                if (type == RtpPacket.PayloadTypeAudio && result.Buffer.Length >= 12)
                {
                    ushort seq = (ushort)((result.Buffer[2] << 8) | result.Buffer[3]);
                    lock (_rtpLock)
                    {
                        // Celowe gubienie (jak shairport diagnostic_drop_packet_fraction)
                        if (DropFraction > 0 && rng.NextDouble() < DropFraction)
 continue; // skip — nie licz, nie zapisuj, nie track seq

 _rtpSequences.Add(seq); // tylko ODEBRANE — do wykrywania gaps
                        _rtpPacketCount++;
                        _rtpPayloads.Add(result.Buffer[12..].ToArray()); // payload bez 12-B RTP header
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }
    }

 /// <summary>Reset RTP counters (on FLUSH — as a receiver clearing its buffer).</summary>
    public void ResetRtpStats()
    {
        lock (_rtpLock)
        {
            _rtpPacketCount = 0;
            _rtpPayloads.Clear();
            _rtpSequences.Clear();
            ResendRecoveredCount = 0;
        }
    }

    /// <summary>
 /// Send retransmission request (85) to the sender control_port for sequence gaps.
    /// Format: 0x80 0xD5 | 0x0001 | first_seq(2) | count(2).
    /// </summary>
    public async Task RequestResendsAsync(CancellationToken ct = default)
    {
        if (_senderControlPort == 0)
            return;

        var missing = new List<ushort>();
        lock (_rtpLock)
        {
            var sorted = _rtpSequences.Distinct().OrderBy(s => s).ToList();
            if (sorted.Count < 2)
                return;
            for (int i = 1; i < sorted.Count; i++)
            {
                int gap = sorted[i] - sorted[i - 1];
                for (int s = sorted[i - 1] + 1; s < sorted[i]; s++)
                    missing.Add((ushort)s);
            }
        }

        if (missing.Count == 0)
            return;

        var ep = new IPEndPoint(IPAddress.Loopback, _senderControlPort);
 // Send from _controlListener (fixed source port = ControlPort) — sender replies 86 there,
 // where ControlListenAsync listens. Batch gaps into max 8-packet requests (like shairport).
        for (int i = 0; i < missing.Count; i += 8)
        {
            var req = new byte[8];
            req[0] = 0x80;
            req[1] = (byte)(0x80 | RtpPacket.PayloadTypeResendRequest); // 0xD5
            req[2] = 0x00; req[3] = 0x01; // nasz seq = 1
            ushort first = missing[i];
            req[4] = (byte)(first >> 8); req[5] = (byte)first;
            ushort count = (ushort)Math.Min(8, missing.Count - i);
            req[6] = (byte)(count >> 8); req[7] = (byte)count;
            await _controlListener!.SendAsync(req, ep, ct).ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var buffer = new byte[8192];
            while (!_cts.IsCancellationRequested)
            {
 // Czytamy request (headery + ew. body)
                using var ms = new MemoryStream();
                int n;
                while (true)
                {
                    n = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (n == 0)
                        return;
                    ms.Write(buffer, 0, n);
                    var data = ms.ToArray();
                    int headerEnd = FindHeaderEnd(data);
                    if (headerEnd >= 0)
                    {
                        var msg = RtspMessage.Parse(data);
                        int expected = headerEnd + 4 + msg.Body.Length;
                        if (data.Length >= expected)
                        {
                            await RespondAsync(stream, msg).ConfigureAwait(false);
                            break;
                        }
                    }
                    if (ms.Length > 64 * 1024)
                        return;
                }
            }
        }
    }

    private async Task RespondAsync(NetworkStream stream, RtspMessage req)
    {
        var requestLine = req.RequestLine;
        LogRequest(requestLine);

        string method = requestLine.Split(' ')[0];
        var resp = new RtspMessage
        {
            RequestLine = "RTSP/1.0 200 OK",
        };

        switch (method)
        {
            case "POST":
                if (requestLine.Contains("/auth-setup"))
                {
 // Symulujemy certyfikat (payload binarny — tu: shortened cert dummy)
                    resp.Body = Encoding.ASCII.GetBytes("DUMMY-APPLE-CERTIFICATE-AIRNEXT-MOCK");
                    resp.Headers["Content-Type"] = "application/octet-stream";
                }
                else
                {
                    resp.RequestLine = "RTSP/1.0 404 Not Found";
                }
                break;

            case "OPTIONS":
                resp.Headers["Public"] = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, POST, GET, PUT";
                break;

            case "ANNOUNCE":
                // Akceptuj SDP; zapisz kodek do logu
                LogRequest("  SDP: " + req.BodyText.Replace("\r\n", " | "));
                break;

            case "SETUP":
                resp.Headers["Transport"] = $"RTP/AVP/UDP;unicast;mode=record;server_port={ServerPort};control_port={ControlPort};timing_port={TimingPort}";
                resp.Headers["Session"] = "1";
                resp.Headers["Audio-Jack-Status"] = "connected; type=analog";
                break;

            case "RECORD":
                resp.Headers["Session"] = "1";
 // WiiM NIE zwraca Audio-Latency — tylko if skonfigurowano
                if (AudioLatencySamples is { } latency)
                    resp.Headers["Audio-Latency"] = latency.ToString();
                break;

            case "FLUSH":
 // Reset receiver buffer (RHI-146) — reply like a real receiver
                resp.Headers["Session"] = "1";
                ResetRtpStats();
                break;

            case "SET_PARAMETER":
                LogRequest("  body: " + req.BodyText.Replace("\r\n", " | "));
                break;

            case "TEARDOWN":
                resp.Headers["Session"] = "1";
                break;
        }

        resp.Headers["Server"] = "AirTunes/366.0";
        resp.Headers["CSeq"] = req.Headers.GetValueOrDefault("CSeq") ?? "1";

        var bytes = resp.Serialize();
        await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
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

    private void LogRequest(string line)
    {
        lock (_lock)
            _log.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {line}");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _rtpListener?.Dispose();
        _controlListener?.Dispose();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (_rtpLoop is not null)
        {
            try { await _rtpLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (_controlLoop is not null)
        {
            try { await _controlLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
