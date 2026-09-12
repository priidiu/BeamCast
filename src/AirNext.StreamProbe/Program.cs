using System.Net;
using AirNext.Audio;
using AirNext.Core.Audio;
using AirNext.Core.Discovery;
using AirNext.Core.Raop;

// StreamProbe — first end-to-end stream to a live receiver:
//   WASAPI capture → FormatConverter (96k/8ch → 44.1k/16/2ch) → ALAC → RTP → WiiM Mini
//
// Usage:
//   dotnet run --project src/AirNext.StreamProbe -c Release -- --host <IP_WiiM> [--port 7000] [--seconds 15]
//   dotnet run --project src/AirNext.StreamProbe -c Release -- --discover [--name "WiiM"] [--seconds 20] (RHI-162)
//
// Flow (docs/wireshark-analysis.md): OPTIONS → ANNOUNCE (ALAC, plaintext) → SETUP → RECORD
// → stream RTP (payload 96) + sync 84 co 1 s → TEARDOWN.

var host = Args("--host", "192.168.1.13");
var port = int.Parse(Args("--port", "7000"));
var seconds = int.Parse(Args("--seconds", "15"));
// --encrypt: AES-CBC (RHI-144) — for et=1 receivers (Apple TV/HomePod)
var encrypt = Environment.GetCommandLineArgs().Contains("--encrypt");
// --discover: auto-discovery przez mDNS (RHI-162) — zamiast --host
var discover = Environment.GetCommandLineArgs().Contains("--discover");
var sampleRate = 44100;
// frameSize = 352 SAMPLES (TuneBlade pcap: ~125 packets/s @44.1k = 352 samples/packet,
// zgodne z fmtp maxFramesPerPacket=352). Large ramki 4096 were odrzucane przez WiiM.
const int frameSize = 352;

if (discover)
{
    var device = await DiscoverDeviceAsync(Args("--name", ""));
    host = device.Host;
    port = device.Port;
    Console.WriteLine($"  [discover] wybrano: {device.Name} ({device.Host}:{device.Port}, {device.ServiceType}, features=0x{device.FeaturesRaw:X})");
    if (device.RequiresEncryption)
    {
        encrypt = true;
        Console.WriteLine("  [discover] device requires encryption (et=1) — enabling AES-CBC");
    }
}

Console.WriteLine($"=== AirNext StreamProbe → {host}:{port} ({seconds}s) ===");

// 1. Sesja RAOP
await using var session = await RaopSession.ConnectAsync(host, port);
Console.WriteLine("  [raop] TCP connected");

// auth-setup WYMAGANY przez WiiM (payload jak TuneBlade — inaczej sesja przechodzi, ale brak audio!)
try
{
    await session.AuthSetupAsync();
    Console.WriteLine("  [raop] auth-setup OK (33 B jak TuneBlade)");
}
catch (Exception ex)
{
    Console.WriteLine($"  [raop] auth-setup: {ex.Message} (CRITICAL — WiiM may produce no audio)");
}

await session.OptionsAsync();
Console.WriteLine("  [raop] OPTIONS OK");

const string path = "3121287335"; // jak w przechwycie TuneBlade
await session.AnnounceAsync(path, encrypt: encrypt);
Console.WriteLine(encrypt
    ? "  [raop] ANNOUNCE (ALAC 16/44.1, AES-CBC encrypted) OK"
    : "  [raop] ANNOUNCE (ALAC 16/44.1, plaintext) OK");

// 2. Sender RTP — dynamiczne porty lokalne (unikamy konfliktu z TuneBlade 6002/6003)
// UWAGA: zdalne endpointy ustawiane dopiero PO SETUP (Connect) — earlier went na port 0!
using var sender = new RtpSender(ssrc: 0x34249563);

// 2b. Serwer timing (NTP master clock) — musi start PRZED SETUP (port idzie do Transport)
sender.StartTimingServer();
Console.WriteLine($"  [raop] timing server start (port {sender.TimingPort})");

// SETUP z dynamicznymi portami sendera (control + timing)
await session.SetupAsync(path, sender.ControlPort, sender.TimingPort);
Console.WriteLine($"  [raop] SETUP OK: server={session.ServerPort} control={session.ControlPort} timing={session.TimingPort}");

// PO SETUP: connect zdalne endpointy (server=audio, control=sync)
sender.Connect(IPAddress.Parse(host), session.ServerPort, session.ControlPort);
Console.WriteLine("  [raop] sender connected (audio→server, sync→control)");

ushort startSeq = 35853;
uint startRtptime = 16441947;
await session.RecordAsync(path, startSeq, startRtptime);
Console.WriteLine($"  [raop] RECORD OK (Audio-Latency: {(session.AudioLatencySamples?.ToString() ?? "brak — WiiM nie zwraca")})");

// --flush: FLUSH after RECORD (receiver buffer reset — track change/pause). RHI-146
if (Environment.GetCommandLineArgs().Contains("--flush"))
{
    await session.FlushAsync(path);
    Console.WriteLine("  [raop] FLUSH OK (reset bufora odbiornika)");
}

// Sync od razu po RECORD — anchor 3 s przed startRtptime (jak TuneBlade: sync.rtptime < audio.rtptime)
uint startAnchor = startRtptime >= (uint)(3 * sampleRate) ? startRtptime - (uint)(3 * sampleRate) : 0;
sender.SendSync(startAnchor, startAnchor + (uint)(3 * sampleRate));
Console.WriteLine($"  [raop] sync #1 sent (seq=7, anchor {startAnchor})");

// SET_PARAMETER volume like TuneBlade (without it WiiM may not start playing)
try
{
    await session.SetParameterAsync(path, "volume: -0.001000");
    Console.WriteLine("  [raop] SET_PARAMETER volume OK");
}
catch (Exception ex)
{
    Console.WriteLine($"  [raop] SET_PARAMETER volume: {ex.Message}");
}

// SET_PARAMETER metadata (x-dmap-tagged) jak TuneBlade — z RTP-Info! (ramka 6276 z pcap)
try
{
    var metadata = BuildDmapMetadata("System Audio", "AirNext");
    await session.SetParameterAsync(path, metadata, "application/x-dmap-tagged", $"rtptime={startRtptime}");
    Console.WriteLine("  [raop] SET_PARAMETER metadata OK (z RTP-Info)");
}
catch (Exception ex)
{
    Console.WriteLine($"  [raop] SET_PARAMETER metadata: {ex.Message}");
}

// SET_PARAMETER progress jak TuneBlade — WiiM liczy czas od tego punktu
try
{
    await session.SetParameterAsync(path, $"progress: {startRtptime}/{startRtptime}/{startRtptime}");
    Console.WriteLine("  [raop] SET_PARAMETER progress OK");
}
catch (Exception ex)
{
    Console.WriteLine($"  [raop] SET_PARAMETER progress: {ex.Message}");
}

// 3. Enkoder ALAC + pipeline
var alac = new AlacEncoder(sampleRate: sampleRate, frameSize: frameSize);
var pcmBuffer = new short[frameSize * 2]; // bufor int16 stereo na one frame ALAC
int pcmFrames = 0;
ushort seq = startSeq;
uint rtptime = startRtptime;
var rtpOut = new byte[alac.MaxOutputBytes];
long packetsSent = 0;
long bytesSent = 0;
var sw = System.Diagnostics.Stopwatch.StartNew();
var syncSw = System.Diagnostics.Stopwatch.StartNew();

// 4. Capture WASAPI
await using var capture = new WasapiLoopbackCapture();
capture.PacketCaptured += packet =>
{
    try
    {
        // WASAPI: float32 interleaved (mix format) → FormatConverter → int16 stereo 44.1k
        var floats = new float[packet.Data.Length / 4];
        var dataSpan = packet.Data.Span;
        for (int i = 0; i < floats.Length; i++)
            floats[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(dataSpan.Slice(i * 4, 4));

        var converted = FormatConverter.ConvertToPcm16Stereo(
            floats, packet.Channels, packet.SampleRate, sampleRate, rng: null);

        // Akumuluj do ramki ALAC (4096 ramek stereo)
        int framesIn = converted.Length / 2;
        int src = 0;
        while (src < framesIn)
        {
            int take = Math.Min(frameSize - pcmFrames, framesIn - src);
            Array.Copy(converted, src * 2, pcmBuffer, pcmFrames * 2, take * 2);
            pcmFrames += take;
            src += take;

            if (pcmFrames == frameSize)
            {
                int n = alac.EncodeFrame(pcmBuffer, rtpOut);
                if (n > 0)
                {
                    if (encrypt)
                        sender.SendEncryptedAudio(seq, rtptime, rtpOut.AsSpan(0, n));
                    else
                        sender.SendAudio(seq, rtptime, rtpOut.AsSpan(0, n), marker: false);
                    seq++;
                    rtptime += frameSize;
                    packetsSent++;
                    bytesSent += n;
                }
                pcmFrames = 0;

 // Sync 84 every ~1 s — seq FIXED 7, ANCHOR 3 s BACK from current rtptime (TuneBlade)
                if (syncSw.ElapsedMilliseconds >= 1000)
                {
 // TuneBlade: sync.rtptime ≈ current_rtptime - 3s, next = anchor + 3s = current
                    uint anchor = rtptime >= (uint)(3 * sampleRate) ? rtptime - (uint)(3 * sampleRate) : 0;
                    sender.SendSync(anchor, anchor + (uint)(3 * sampleRate));
                    syncSw.Restart();
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [pipeline] ERROR: {ex.Message}");
    }
};

await capture.StartAsync();
Console.WriteLine("  [wasapi] capture start — play audio!");

await Task.Delay(TimeSpan.FromSeconds(seconds));
await capture.StopAsync();
sw.Stop();

// Send remainder (partial ramka)
if (pcmFrames > 0)
{
    var partial = new short[pcmFrames * 2];
    Array.Copy(pcmBuffer, partial, pcmFrames * 2);
    int n = alac.EncodeFrame(partial, rtpOut);
    if (n > 0)
    {
        if (encrypt)
            sender.SendEncryptedAudio(seq, rtptime, rtpOut.AsSpan(0, n));
        else
            sender.SendAudio(seq, rtptime, rtpOut.AsSpan(0, n), marker: false);
        packetsSent++;
        bytesSent += n;
    }
}

// 5. TEARDOWN
try
{
    await session.TeardownAsync(path);
    Console.WriteLine("  [raop] TEARDOWN OK");
}
catch (Exception ex)
{
    Console.WriteLine($"  [raop] TEARDOWN: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine($"=== RESULT: {seconds}s, {packetsSent} RTP packets, {bytesSent} B ({bytesSent / 1024.0:F1} KB), {sw.Elapsed.TotalSeconds:F1}s ===");
Console.WriteLine($"  Timing replies (82→83): {sender.TimingRepliesSent}");
Console.WriteLine($"  Glitchy (retransmisje 85 od odbiorcy): {sender.ResendRequestsHandled}");
Console.WriteLine(packetsSent > 0
    ? "If you hear audio from the WiiM — M1 end-to-end WORKS ✅"
    : "No packets — check that audio is playing and capture is receiving (CaptureProbe).");

static string Args(string name, string def)
{
    var a = Environment.GetCommandLineArgs();
    for (int i = 0; i < a.Length - 1; i++)
        if (a[i] == name) return a[i + 1];
    return def;
}

/// <summary>
/// Buduje metadata DMAP-tagged jak TuneBlade z pcap:
///   mlit{ minm"System Audio\0" asar"TuneBlade\0" asal"\0" }
/// Format: tag(4B) + length(4B, big-endian) + data (string z null terminatorem).
/// UWAGA: BEZ 4 bytes zero po length — to was bug causing HTTP 400.
/// </summary>
static byte[] BuildDmapMetadata(string title, string artist)
{
    static byte[] DmapTag(string tag, string value)
    {
        var val = System.Text.Encoding.UTF8.GetBytes(value);
        var data = new byte[val.Length + 1]; // + null terminator
        val.CopyTo(data, 0);
        var result = new byte[8 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(result, 0);
        result[4] = (byte)(data.Length >> 24);
        result[5] = (byte)(data.Length >> 16);
        result[6] = (byte)(data.Length >> 8);
        result[7] = (byte)data.Length;
        data.CopyTo(result, 8);
        return result;
    }

    var minm = DmapTag("minm", title);
    var asar = DmapTag("asar", artist);
    var asal = DmapTag("asal", ""); // pusty album → samo \0

 // mlit: 'mlit' + 4B length + contents (bez dodatkowych zer)
    int contentLen = minm.Length + asar.Length + asal.Length;
    var mlit = new byte[8 + contentLen];
    System.Text.Encoding.ASCII.GetBytes("mlit").CopyTo(mlit, 0);
    mlit[4] = (byte)(contentLen >> 24);
    mlit[5] = (byte)(contentLen >> 16);
    mlit[6] = (byte)(contentLen >> 8);
    mlit[7] = (byte)contentLen;
    minm.CopyTo(mlit, 8);
    asar.CopyTo(mlit, 8 + minm.Length);
    asal.CopyTo(mlit, 8 + minm.Length + asar.Length);

    return mlit;
}

/// <summary>
/// Auto-discovery przez mDNS (RHI-162): find device AirPlay (_airplay._tcp + _raop._tcp),
/// wybierz po nazwie (--name) albo najlepsze RAOP (port 7000). Czeka do ~12 s.
/// Preferencje wyboru: 1) --name match, 2) port 7000 (RAOP/AP1), 3) pierwsze znalezione.
/// </summary>
static async Task<AirplayDevice> DiscoverDeviceAsync(string? nameFilter)
{
    Console.WriteLine("  [discover] searching for AirPlay devices via mDNS (_airplay._tcp + _raop._tcp)...");

 // Windows: natywny DNS-SD disabled tymczasowo (0xC0000005 AV w DnsServiceBrowse —
 // nie do debugowania bez Win11). Own socket multicast WORKS (found Orange w eaa6214).
    bool useNative = false;
    var found = new List<AirplayDevice>();
    var tcs = new TaskCompletionSource<AirplayDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
    List<string> diagnostics = new();

    if (useNative)
    {
        using var browser = new NativeMdnsBrowser();
        browser.DeviceAdded += d =>
        {
            lock (found)
            {
                found.Add(d);
                Console.WriteLine($"  [discover] znaleziono: {d.Name} ({d.Host}:{d.Port}, {d.ServiceType})");
            }
            if (!string.IsNullOrEmpty(nameFilter) && d.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(d);
        };
        browser.Start();
        return await WaitForDeviceAsync(found, tcs, browser.Diagnostics, nameFilter).ConfigureAwait(false);
    }
    else
    {
        using var browser = new MdnsBrowser();
        browser.DeviceAdded += d =>
        {
            lock (found)
            {
                found.Add(d);
                Console.WriteLine($"  [discover] znaleziono: {d.Name} ({d.Host}:{d.Port}, {d.ServiceType})");
            }
            if (!string.IsNullOrEmpty(nameFilter) && d.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(d);
        };
        browser.Start(queryInterval: TimeSpan.FromSeconds(1));
        return await WaitForDeviceAsync(found, tcs, browser.Diagnostics, nameFilter, browser).ConfigureAwait(false);
    }
}

/// <summary>Czeka na device: --name match → port 7000 (RAOP) → timeout → najlepsze.</summary>
static async Task<AirplayDevice> WaitForDeviceAsync(
    List<AirplayDevice> found,
    TaskCompletionSource<AirplayDevice> tcs,
    List<string> diagnostics,
    string? nameFilter,
    MdnsBrowser? ownBrowser = null)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (DateTime.UtcNow < deadline && !tcs.Task.IsCompleted)
    {
        AirplayDevice? best = null;
        lock (found)
        {
            best = found.FirstOrDefault(x => x.Port == 7000); // RAOP/AP1 (WiiM)
        }
        if (best is not null)
            return best;

        await Task.Delay(250);
    }

    if (tcs.Task.IsCompleted)
        return await tcs.Task;

    // Timeout — wybierz najlepsze z tego co jest
    lock (found)
    {
        Console.WriteLine("  [discover] DIAGNOSTYKA:");
        foreach (var d in diagnostics)
            Console.WriteLine($"    {d}");
        if (ownBrowser is not null && ownBrowser.PacketsReceived > 0)
            Console.WriteLine($"    odebrane pakiety mDNS: {ownBrowser.PacketsReceived}");

        var best = found.FirstOrDefault(x => x.Port == 7000)
            ?? found.FirstOrDefault(x => x.ServiceType == "airplay")
            ?? found.FirstOrDefault();

        if (best is not null)
        {
            Console.WriteLine($"  [discover] wybieram najlepsze: {best.Name} ({best.Host}:{best.Port})");
            return best;
        }
    }

    throw new InvalidOperationException("No AirPlay devices found (mDNS timeout 15 s). Check network/WiiM.");
}
