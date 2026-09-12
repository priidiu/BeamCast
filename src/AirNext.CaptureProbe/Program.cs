using AirNext.Audio;
using AirNext.Core.Audio;

// Capture Probe (RHI-136 runtime test) — uruchom na Windows 11:
//   dotnet run --project src/AirNext.CaptureProbe -c Release
// Play any audio in the background (YouTube/music) — probe counts WASAPI loopback packets for 3 s.
// PASS = packets received with a valid MixFormat and QPC timestamps.
//
// Tryb self-test latencji (RHI-140):
//   dotnet run --project src/AirNext.CaptureProbe -c Release -- --latency-test
// Plays a 1 s sweep on the default device, records loopback, measures xcorr → ms → PASS/FAIL.

var latencyTest = Environment.GetCommandLineArgs().Contains("--latency-test");
if (latencyTest)
    return await RunLatencyTestAsync();

Console.WriteLine("=== AirNext Capture Probe (WASAPI loopback, RHI-136) ===");
Console.WriteLine("Play audio (YouTube/music) within 3 seconds...\n");

await using var capture = new WasapiLoopbackCapture();

int packetCount = 0;
long totalBytes = 0;
long firstQpc = 0;
long lastQpc = 0;
var firstFormat = AudioFormat.MixDefault;

capture.PacketCaptured += p =>
{
    Interlocked.Increment(ref packetCount);
    Interlocked.Add(ref totalBytes, p.Data.Length);
    Interlocked.Exchange(ref firstQpc, firstQpc == 0 ? p.QpcTimestamp : firstQpc);
    Interlocked.Exchange(ref lastQpc, p.QpcTimestamp);
    firstFormat = new AudioFormat(p.SampleRate, p.BitDepth, p.Channels);
};

var sw = System.Diagnostics.Stopwatch.StartNew();
await capture.StartAsync();
await Task.Delay(3000);
await capture.StopAsync();
sw.Stop();

Console.WriteLine($"MixFormat: {firstFormat.SampleRate} Hz / {firstFormat.BitDepth} bit / {firstFormat.Channels} ch");
Console.WriteLine($"Pakiety odebrane: {packetCount}");
Console.WriteLine($"Bajty: {totalBytes} ({totalBytes / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine($"Czas: {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"QPC first: {firstQpc}  last: {lastQpc}  (delta: {lastQpc - firstQpc})");

bool pass = packetCount > 0 && firstQpc != 0;
Console.WriteLine();
Console.WriteLine(pass
    ? "PASS ✅ — WASAPI loopback works: packets + QPC timestamps OK."
    : "FAIL ❌ — no packets. Are you playing audio? Check the default render device (eRender/eConsole).");
return pass ? 0 : 1;

/// <summary>Self-test latencji (RHI-140): sweep → loopback → xcorr → ms → PASS/FAIL.</summary>
static async Task<int> RunLatencyTestAsync()
{
    Console.WriteLine("=== AirNext Self-Test Latencji (RHI-140) ===");
    Console.WriteLine("Odtwarzam sweep 1 s (WASAPI render) i nagrywam loopback...\n");

    // 1. Record loopback immediately (render + capture concurrently)
    await using var cap = new WasapiLoopbackCapture();
    var capturedChunks = new List<byte[]>();
    cap.PacketCaptured += p => { lock (capturedChunks) capturedChunks.Add(p.Data.ToArray()); };

    // 2. Open renderer in the MIX FORMAT (GetMixFormat — same as capture)
    using var renderer = new WasapiRenderer();
    renderer.Open();
    int renderRate = renderer.SampleRate;
    int renderCh = renderer.Channels;
    Console.WriteLine($"Mix render: {renderRate} Hz / {renderCh} ch");

    // Sweep in mix format (mono → expanded to all channels)
    var sweep = LatencyMeasurer.GenerateSweep(renderRate, seconds: 1.0);
    var sweepInterleaved = new float[sweep.Length * renderCh];
    for (int i = 0; i < sweep.Length; i++)
        for (int ch = 0; ch < renderCh; ch++)
            sweepInterleaved[i * renderCh + ch] = sweep[i];

    var sw = System.Diagnostics.Stopwatch.StartNew();
    await cap.StartAsync();

    renderer.Start(sweepInterleaved);
    await Task.Delay(2500); // sweep 1 s + margines 1.5 s na bufor render/loopback
    await renderer.StopAsync();
    await cap.StopAsync();
    sw.Stop();

    // 3. Zbierz nagranie → float32 (mix format 96k/8ch)
    int totalBytes = capturedChunks.Sum(c => c.Length);
    var raw = new byte[totalBytes];
    int off = 0;
    foreach (var c in capturedChunks) { c.CopyTo(raw, off); off += c.Length; }
    var floats = new float[raw.Length / 4];
    for (int i = 0; i < floats.Length; i++)
        floats[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4, 4));

    // 4. Convert to 44.1k/2ch (like StreamProbe) — measure the full chain
    const int rate = 44100;
    var pcm = FormatConverter.ConvertToPcm16Stereo(floats, cap.MixFormat.Channels, cap.MixFormat.SampleRate, rate, rng: null);
    var capturedF = new float[pcm.Length];
    for (int i = 0; i < capturedF.Length; i++)
        capturedF[i] = pcm[i] / 32768f;

    // Referencja do xcorr w 44.1k (sweep z renderRate → 44.1k przez FormatConverter — te same kroki co nagranie)
    var sweepPcm = FormatConverter.ConvertToPcm16Stereo(sweep, renderCh, renderRate, rate, rng: null);
    var sweepRef = new float[sweepPcm.Length];
    for (int i = 0; i < sweepRef.Length; i++)
        sweepRef[i] = sweepPcm[i] / 32768f;

    // 5. Xcorr + peak-strength diagnostics
    int maxDelay = Math.Min(capturedF.Length - sweepRef.Length, 3 * rate); // max 3 s
    var result = maxDelay > 0
        ? LatencyMeasurer.FindDelayWithStrength(sweepRef, capturedF, maxDelay)
        : new LatencyMeasurer.DelayResult(-1, 0, 0, 0);
    double ms = LatencyMeasurer.DelayMs(result.DelaySamples, rate);
    bool pass = LatencyMeasurer.IsPass(ms) && result.PeakRatio > 2.0; // silny peak wymagany

    Console.WriteLine($"Recorded: {floats.Length} mix samples ({cap.MixFormat.SampleRate}/{cap.MixFormat.BitDepth}/{cap.MixFormat.Channels}), converted → {pcm.Length / 2} ramek 44.1k");
    Console.WriteLine($"Xcorr: delay={result.DelaySamples} samples ({ms:F1} ms), peak ratio={result.PeakRatio:F1}");
    Console.WriteLine();
    Console.WriteLine(pass
        ? $"PASS ✅ — latencja capture {ms:F1} ms (< 40 ms, NFR-1)."
        : result.DelaySamples < 0 || result.PeakRatio <= 2.0
            ? $"FAIL ❌ — no reliable peak (ratio {result.PeakRatio:F1}). Is the sweep audible (speakers not muted)?"
            : $"FAIL ❌ — latency {ms:F1} ms >= 40 ms. Check WASAPI buffer/driver.");

    return pass ? 0 : 1;
}
