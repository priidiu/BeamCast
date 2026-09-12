using System.Net;
using System.Net.Sockets;

namespace AirNext.Core.Raop;

/// <summary>
/// Send RTP to a RAOP receiver (RHI-139):
///  - audio (payload 96) na server_port,
/// - sync (84) na control_port (raz na second),
///  - timing 82/83 on timing_port (NTP server — answers receiver type-82 requests).
/// Pacer: tempo wg zegara audio (QPC→RTP ts), tryb podstawowy — sleep na sample.
/// </summary>
public sealed class RtpSender : IDisposable
{
    private readonly UdpClient _audio;
    private readonly UdpClient _control;
    private IPEndPoint? _serverEp;
    private IPEndPoint? _controlEp;

    private UdpClient? _timing;
    private CancellationTokenSource? _timingCts;
    private Task? _timingTask;

 // Active timing client (RHI-172): send type-82 to the receiver, measure RTT from 83
    private UdpClient? _timingClient;
    private IPEndPoint? _timingReceiverEp;
    private CancellationTokenSource? _timingClientCts;
    private Task? _timingClientTask;

    /// <summary>Zmierzona jednokierunkowa latencja sieci w ms (-1 = brak danych).</summary>
    public double MeasuredLatencyMs { get; private set; } = -1;

 // Retransmisje 85/86 (RHI-143): bufor ostatnich packets audio (okno ~1 s = ~125 pkt @125 pkt/s)
    private const int ResendWindowPackets = 256;
    private readonly Dictionary<ushort, byte[]> _resendBuffer = new();
    private readonly object _resendLock = new();
    private CancellationTokenSource? _resendCts;
    private Task? _resendTask;

 // Zegar NTP: TuneBlade uses SMALL licznika (0x83aa80c0 ≈ 2208988800 + uptime),
    // NIE prawdziwego czasu 2026 — WiiM koreluje rtptime↔NTP i z prawdziwym NTP
 // (56 lat do przodu) nigdy nie zaczyna play. Startujemy od Unix epoch + uptime.
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private const ulong NtpUnixEpochSeconds = 2208988800UL;

    public uint Ssrc { get; }

    /// <summary>Lokalny port control (dynamiczny — przekazywany do SETUP).</summary>
    public int ControlPort { get; }

 /// <summary>Port listen serwera timing (dynamiczny — przekazywany do SETUP).</summary>
    public int TimingPort { get; private set; }

 /// <summary>Liczba odebranych requests timing (82) i sent reply (83) — diagnostyka.</summary>
    public long TimingRepliesSent { get; private set; }

    public RtpSender(uint ssrc)
    {
        Ssrc = ssrc;
        _audio = new UdpClient(AddressFamily.InterNetwork);
        // Dynamiczne porty lokalne — unikamy konfliktu z TuneBlade (6002/6003)
        _control = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        ControlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;
    }

    /// <summary>
 /// Set remote receiver endpoints — MUST be called AFTER SETUP
 /// (porty server/control z odpowiedzi SETUP). Previously send went na port 0!
 /// Automatycznie startuje listen na retransmisje 85/86 (RHI-143).
 /// If podano timingPort — startuje aktywny timing client (RHI-172).
    /// </summary>
    public void Connect(IPAddress host, ushort serverPort, ushort controlPort, ushort timingPort = 0)
    {
        _serverEp = new IPEndPoint(host, serverPort);
        _controlEp = new IPEndPoint(host, controlPort);
        StartResendListener();

        if (timingPort > 0)
            StartTimingClient(host, timingPort);
    }

    /// <summary>
 /// Start listen na control_port na requesty retransmisji (85) → odpowiedzi 86.
 /// Receiver drops a packet → sends 85 (first_seq, count) → sender replies 86 from the buffer.
    /// </summary>
    public void StartResendListener()
    {
        if (_resendTask is not null)
            return;

        _resendCts = new CancellationTokenSource();
        _resendTask = Task.Run(() => ResendLoopAsync(_resendCts.Token), CancellationToken.None);
    }

 /// <summary>Liczba handled requests retransmisji (85) — diagnostyka.</summary>
    public long ResendRequestsHandled { get; private set; }

    private async Task ResendLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _control.ReceiveAsync(ct).ConfigureAwait(false);
                var req = result.Buffer;
                // Request 85: 0x80 0xD5 | 0x0001 | first_seq(2) | count(2) — 8 B
                if (req.Length >= 8 && (req[1] & 0x7F) == RtpPacket.PayloadTypeResendRequest)
                {
                    ushort firstSeq = (ushort)((req[4] << 8) | req[5]);
                    ushort count = (ushort)((req[6] << 8) | req[7]);
                    ResendRequestsHandled++;

                    byte[][] replies;
                    lock (_resendLock)
                    {
                        replies = new byte[count][];
                        for (int i = 0; i < count; i++)
                        {
                            ushort seq = (ushort)(firstSeq + i);
                            replies[i] = _resendBuffer.TryGetValue(seq, out var pkt)
                                ? RtpPacket.BuildResendReply(pkt)
                                : Array.Empty<byte>();
                        }
                    }

                    foreach (var reply in replies)
                    {
                        if (reply.Length == 0)
                            continue;
                        _control.Send(reply, reply.Length, result.RemoteEndPoint);
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

 /// <summary>NTP 64-bit z internal zegara (Unix epoch + uptime — jak TuneBlade).</summary>
    private ulong NowNtp()
    {
        ulong seconds = NtpUnixEpochSeconds + (ulong)(_clock.Elapsed.TotalSeconds);
        ulong fraction = (ulong)((_clock.Elapsed.TotalSeconds - Math.Floor(_clock.Elapsed.TotalSeconds)) * 4294967296.0);
        return (seconds << 32) | fraction;
    }

 /// <summary>Send an audio packet (payload 96) on server_port. Store in the resend buffer.</summary>
    public void SendAudio(ushort seq, uint rtptime, ReadOnlySpan<byte> payload, bool marker = false)
    {
        if (_serverEp is null)
            throw new InvalidOperationException("RtpSender not connected — call Connect() after SETUP");
        var packet = RtpPacket.BuildAudio(seq, rtptime, Ssrc, marker, payload);
        _audio.Send(packet, packet.Length, _serverEp);

        lock (_resendLock)
        {
            _resendBuffer[seq] = packet;
            if (_resendBuffer.Count > ResendWindowPackets)
            {
 // drop najstarsze (iteracja po kluczach — seq rising)
                var oldest = _resendBuffer.Keys.OrderBy(k => k).First();
                _resendBuffer.Remove(oldest);
            }
        }
    }

    /// <summary>
 /// Send an AES-CBC encrypted audio packet (RHI-144): 4 B pre-header + 12 B RTP + CBC payload.
 /// Layout matches TuneBlade (processPCMAndEnqueue). Resend buffer keeps the full packet.
    /// </summary>
    public void SendEncryptedAudio(ushort seq, uint rtptime, ReadOnlySpan<byte> alacPayload)
    {
        if (_serverEp is null)
            throw new InvalidOperationException("RtpSender not connected — call Connect() after SETUP");
        var packet = AesEncryption.EncryptAudioPacket(seq, rtptime, Ssrc, alacPayload);
        _audio.Send(packet, packet.Length, _serverEp);

        lock (_resendLock)
        {
            _resendBuffer[seq] = packet;
            if (_resendBuffer.Count > ResendWindowPackets)
            {
                var oldest = _resendBuffer.Keys.OrderBy(k => k).First();
                _resendBuffer.Remove(oldest);
            }
        }
    }

 /// <summary>Send a sync packet (84) on control_port — once per second. Seq FIXED (7) like TuneBlade.</summary>
    public void SendSync(uint rtptime, uint nextRtptime, ushort seq = 7)
    {
        if (_controlEp is null)
            throw new InvalidOperationException("RtpSender not connected — call Connect() after SETUP");
        var ntp = NowNtp();
        var packet = RtpPacket.BuildSync(seq, rtptime, ntp, nextRtptime);
        _control.Send(packet, packet.Length, _controlEp);
    }

    /// <summary>
    /// Start serwera timing (NTP master clock) na dynamicznym porcie lokalnym.
 /// Receiver (WiiM) sends type-82 → we reply 83 (originate+receive+transmit NTP).
    /// Without this the receiver drifts → clicks. (docs/wireshark-analysis.md §5)
    /// </summary>
    public void StartTimingServer()
    {
        if (_timing is not null)
            return;

        _timing = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        TimingPort = ((IPEndPoint)_timing.Client.LocalEndPoint!).Port;
        _timingCts = new CancellationTokenSource();
        _timingTask = Task.Run(() => TimingLoopAsync(_timingCts.Token), CancellationToken.None);
    }

    private async Task TimingLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested && _timing is not null)
        {
            try
            {
                var result = await _timing.ReceiveAsync(ct).ConfigureAwait(false);
                if (result.Buffer.Length >= 20 && (result.Buffer[1] & 0x7F) == RtpPacket.PayloadTypeTimingRequest)
                {
 // Request 82: RTP header (8 B) + NTP timestamp (8 B) — response 83:
                    // RTP header (8 B, PT=83) + originate (8 B, z requestu) + receive (8 B) + transmit (8 B)
                    var req = result.Buffer;
                    var reply = new byte[32];
                    reply[0] = 0x80;
                    reply[1] = (byte)(1 << 7 | RtpPacket.PayloadTypeTimingReply); // M=1, PT=83
                    reply[2] = req[2];
                    reply[3] = req[3];
                    // reply[4..7] = RTP ts = 0
                    // originate: NTP z requestu (ostatnie 8 B requestu)
                    int reqNtpOffset = req.Length >= 28 ? req.Length - 8 : 12;
                    Array.Copy(req, reqNtpOffset, reply, 8, 8);

                    var now = NowNtp();
                    WriteUInt64BE(reply, 16, now); // receive
                    WriteUInt64BE(reply, 24, now); // transmit

                    _timing.Send(reply, reply.Length, result.RemoteEndPoint);
                    TimingRepliesSent++;
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

 /// <summary>Difference two timestamps NTP 64-bit w sekundach (handles wrapping).</summary>
    private static double NtpDiff(ulong later, ulong earlier)
    {
        long diff = (long)later - (long)earlier;
        return diff / 4294967296.0; // 2^32
    }

    /// <summary>
 /// Start the active timing client (RHI-172): send type-82 to the receiver every 2s,
 /// mierzy RTT z reply 83. One-way ≈ RTT/2 (assumption: symetryczna network).
    /// </summary>
    public void StartTimingClient(IPAddress receiverHost, ushort receiverTimingPort)
    {
        if (_timingClient is not null) return;

        _timingClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _timingReceiverEp = new IPEndPoint(receiverHost, receiverTimingPort);
        _timingClientCts = new CancellationTokenSource();
        _timingClientTask = Task.Run(() => TimingClientLoopAsync(_timingClientCts.Token), CancellationToken.None);
    }

    private async Task TimingClientLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[256];
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested && _timingClient is not null)
        {
            try
            {
                // Request 82: RTP header (8B) + NTP timestamp (8B) = 20B
                var t1 = NowNtp();
                var request = new byte[20];
                request[0] = 0x80;  // V=2
                request[1] = RtpPacket.PayloadTypeTimingRequest; // PT=82
                // seq[2..3] = 0, timestamp[4..7] = 0
                WriteUInt64BE(request, 12, t1);

                _timingClient.Send(request, request.Length, _timingReceiverEp!);

                // Czekaj na reply (83) z timeout 2s
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                var result = await _timingClient.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var t4 = NowNtp();

                if (result.Buffer.Length >= 32 && (result.Buffer[1] & 0x7F) == RtpPacket.PayloadTypeTimingReply)
                {
                    ulong originate = ReadUInt64BE(result.Buffer, 8);  // T1 echoed
                    ulong receive = ReadUInt64BE(result.Buffer, 16);   // T2 (receiver clock)
                    ulong transmit = ReadUInt64BE(result.Buffer, 24);  // T3 (receiver clock)

                    // Standard NTP: RTT = (T4-T1) - (T3-T2)
                    double rtt = NtpDiff(t4, t1) - NtpDiff(transmit, receive);
                    MeasuredLatencyMs = Math.Max(0, rtt / 2.0 * 1000.0);
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                consecutiveFailures++;
                if (consecutiveFailures > 3)
                    MeasuredLatencyMs = -1;
            }
            catch (SocketException)
            {
                consecutiveFailures++;
                if (consecutiveFailures > 3)
                    MeasuredLatencyMs = -1;
            }

            // Probe co 2 sekundy
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
 /// Podstawowy pacer: sends stream packets w tempie zegara audio.
    /// samplesPerPacket = 352 (ALAC 16/44.1), sampleRate = 44100 → ~8 ms/packet (TuneBlade).
    /// </summary>
    public async Task StreamAsync(
        ushort startSeq,
        uint startRtptime,
        int samplesPerPacket,
        int sampleRate,
        int packetCount,
        Func<uint, byte[]> payloadFactory,
        CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds((double)samplesPerPacket / sampleRate);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        uint rtptime = startRtptime;
        ushort seq = startSeq;
        long expectedTicks = 0;

        for (int i = 0; i < packetCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            SendAudio(seq, rtptime, payloadFactory(rtptime), marker: i == 0);
            seq++;
            rtptime += (uint)samplesPerPacket;
            expectedTicks += interval.Ticks;
            var delay = TimeSpan.FromTicks(expectedTicks - stopwatch.Elapsed.Ticks);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _timingCts?.Cancel();
        _resendCts?.Cancel();
        _audio.Dispose();
        _control.Dispose();
        _timing?.Dispose();
        if (_timingTask is not null)
        {
            try { _timingTask.Wait(500); } catch { /* ignore */ }
        }
        if (_resendTask is not null)
        {
            try { _resendTask.Wait(500); } catch { /* ignore */ }
        }
        // Timing client (RHI-172)
        _timingClientCts?.Cancel();
        _timingClient?.Dispose();
        if (_timingClientTask is not null)
        {
            try { _timingClientTask.Wait(500); } catch { /* ignore */ }
        }
        _timingCts?.Dispose();
        _resendCts?.Dispose();
        _timingClientCts?.Dispose();
    }
}
