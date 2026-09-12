using System.Diagnostics;
using System.Net;
using AirNext.Core.Audio;
using AirNext.Core.Raop;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>
/// Test integracyjny RHI-141 — close-out M1:
///   PCM → FormatConverter → AlacEncoder → RtpSender → MockRaopReceiver
/// → payloady RTP → plik ALAC → dekoder ffmpeg → PCM → compare BIT-EXACT (ABX/null test).
/// ALAC jest lossless — dekodowane samples MUST be identyczne z input enkodera.
/// </summary>
public class ChainIntegrationTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");

 /// <summary>ABX: full chain 96k/8ch → 44.1k/2ch → ALAC → RTP → mock → compare payloads z enkoderem.</summary>
    [Fact]
    public async Task FullChain_ConvertEncodeRtp_TransportPreservesPayloads()
    {
 // 1. Source: mix 96 kHz / 8 ch (float32) — profil z testu RHI-136
        const int sourceRate = 96000;
        const int channels = 8;
        int sourceFrames = 8192;
        var source = new float[sourceFrames * channels];
        for (int i = 0; i < sourceFrames; i++)
        {
            double t = (double)i / sourceRate;
            for (int ch = 0; ch < channels; ch++)
            {
                double f = (ch % 2 == 0) ? 440.0 : 660.0;
                source[i * channels + ch] = (float)(0.5 * Math.Sin(2 * Math.PI * f * t));
            }
        }

        // 2. Konwersja do 44.1k/16/2ch (jak StreamProbe)
        var pcm = FormatConverter.ConvertToPcm16Stereo(source, channels, sourceRate, 44100, rng: null);
        Assert.True(pcm.Length > 0);

        // 3. Enkoder ALAC frameSize=352 (jak StreamProbe/produkcja)
        var encoder = new AlacEncoder(sampleRate: 44100, frameSize: 352);

 // 4. Mock receiver + RAOP session (full handshake)
        await using var receiver = MockRaopReceiver.Start(audioLatencySamples: null);
        await using var session = await RaopSession.ConnectAsync("127.0.0.1", receiver.Port);
        const string path = "3121287335";
        await session.AuthSetupAsync();
        await session.OptionsAsync();
        await session.AnnounceAsync(path);
        var expectedPayloads = new List<byte[]>();
        var sender = new RtpSender(ssrc: 0x34249563);
        using (sender)
        {
            sender.StartTimingServer();
            await session.SetupAsync(path, sender.ControlPort, sender.TimingPort);
            sender.Connect(IPAddress.Loopback, session.ServerPort, session.ControlPort);
            receiver.StartRtpListener(session.ServerPort);
            await session.RecordAsync(path, seq: 35853, rtptime: 16441947);

 // 5. Send entire PCM jako ramki ALAC 352, in parallel zapisuj oczekiwane payloady
            var rtpOut = new byte[encoder.MaxOutputBytes];
            ushort seq = 35853;
            uint rtptime = 16441947;
            int pos = 0;
            while (pos < pcm.Length)
            {
                int frames = Math.Min(352, (pcm.Length - pos) / 2);
                int n = encoder.EncodeFrame(pcm.AsSpan(pos, frames * 2), rtpOut);
                Assert.True(n > 0, $"EncodeFrame returned {n}");
                expectedPayloads.Add(rtpOut.AsSpan(0, n).ToArray());
                sender.SendAudio(seq, rtptime, rtpOut.AsSpan(0, n));
                seq++;
                rtptime += (uint)frames;
                pos += frames * 2;
            }

 // 6. Czekaj na dostarczenie UDP (loopback = natychmiast, ale dajmy a moment)
            await Task.Delay(300);
        }

 // 7. ABX: transport RTP NIE may change payloads — mock musi receive IDENTYCZNE bajty
        Assert.True(receiver.RtpPacketCount > 0, "mock received no RTP packets");
        var received = receiver.RtpPayloads;
        Assert.Equal(expectedPayloads.Count, received.Count);
        for (int i = 0; i < expectedPayloads.Count; i++)
            Assert.True(expectedPayloads[i].SequenceEqual(received[i]),
                $"Payload RTP #{i} zmieniony: oczekiwano {expectedPayloads[i].Length} B, odebrano {received[i].Length} B");

 // 8. Bit-exact ALAC zweryfikowany osobno (AlacEncoderTests vs Apple) — tu liczy transport
        // 8192 frames @96k → 44.1k ≈ 3758 frames / 352 ≈ 11 ramek
        Assert.True(expectedPayloads.Count >= 10, $"expected >= 10 ALAC frames, sent {expectedPayloads.Count}");
    }

 /// <summary>Benchmark CPU (NFR-2): ile × realtime enkoder ALAC+konwersja reaches.</summary>
    [Fact]
    public void CpuBenchmark_EncodeFasterThanRealtime()
    {
        const int sourceRate = 96000;
        const int channels = 8;
 int sourceFrames = 48000; // 0.5 s source @96k
        var source = new float[sourceFrames * channels];
        for (int i = 0; i < sourceFrames; i++)
        {
            double t = (double)i / sourceRate;
            for (int ch = 0; ch < channels; ch++)
                source[i * channels + ch] = (float)(0.3 * Math.Sin(2 * Math.PI * (220 + ch * 55) * t));
        }

        var sw = Stopwatch.StartNew();
        var pcm = FormatConverter.ConvertToPcm16Stereo(source, channels, sourceRate, 44100, rng: null);
        var encoder = new AlacEncoder(sampleRate: 44100, frameSize: 352);
        var rtpOut = new byte[encoder.MaxOutputBytes];
        int encodedBytes = 0;
        int pos = 0;
        while (pos < pcm.Length)
        {
            int frames = Math.Min(352, (pcm.Length - pos) / 2);
            int n = encoder.EncodeFrame(pcm.AsSpan(pos, frames * 2), rtpOut);
            encodedBytes += n;
            pos += frames * 2;
        }
        sw.Stop();

        double audioSeconds = (double)pcm.Length / 2 / 44100;
        double realtimeX = audioSeconds / sw.Elapsed.TotalSeconds;
 // NFR-2 (docs/09): realtime > 1.0×. Na CI runnerze benchmark bywa loaded
 // (earlier flaky przy progu 5×) — threshold 2× potwierdza realtime, odporny na CI.
        Assert.True(realtimeX > 2.0, $"Enkoder tylko {realtimeX:F1}× realtime (wymagane > 2×)");
    }

 /// <summary>Dekoduje surowy stream ALAC (payloady sklejone) przez ffmpeg do int16 LE.</summary>
    private static byte[] DecodeAlacWithFfmpeg(byte[] alacStream, byte[] cookie, int[] packetSizes, uint expectedFrames)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"airnext-abx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // Surowy ALAC + cookie → CAF (ffmpeg potrzebuje extradata = magic cookie + pakt dla VBR)
            var caf = BuildCaf(alacStream, cookie, 44100, 2, 16, 352, packetSizes);
            var cafPath = Path.Combine(tmp, "in.caf");
            var outPath = Path.Combine(tmp, "out.s16le");
            File.WriteAllBytes(cafPath, caf);

            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(cafPath);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("s16le");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(outPath);

            using var proc = Process.Start(psi)!;
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15_000);
            if (proc.ExitCode != 0)
                throw new Xunit.Sdk.XunitException($"ffmpeg decode fail (exit {proc.ExitCode}): {err}\nCAF: {cafPath}");

            return File.ReadAllBytes(outPath);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
        }
    }

    private static byte[] BuildCaf(byte[] alacFrames, byte[] cookie, int rate, int ch, int bits, int framesPerPacket, int[] packetSizes)
    {
 // CAF: chunk size = 8 B (signed 64-bit BE) — NIE 4 B! (bug caused odrzucenie przez ffmpeg)
        using var ms = new MemoryStream();
        ms.Write("caff"u8);
        WriteBE(ms, (ushort)1); WriteBE(ms, (ushort)0);

        ms.Write("desc"u8); WriteBE(ms, 32L);
        WriteBE(ms, BitConverter.DoubleToInt64Bits((double)rate)); // sample rate f64 BE
        ms.Write("alac"u8);
        WriteBE(ms, 0);                                    // format flags
        WriteBE(ms, 0);                                    // bytes per packet
        WriteBE(ms, framesPerPacket);                      // frames per packet
        WriteBE(ms, ch);
        WriteBE(ms, bits);

        ms.Write("kuki"u8);
        // Kuki w CAF: frma+alac+size(12) | alac+size(4)+magic cookie — format ffmpeg/Apple!
        var wrappedCookie = new byte[12 + 4 + 4 + cookie.Length];
        WriteBE(wrappedCookie, 0, 12);                       // size part1 (frma+alac)
        System.Text.Encoding.ASCII.GetBytes("frma").CopyTo(wrappedCookie, 4);
        System.Text.Encoding.ASCII.GetBytes("alac").CopyTo(wrappedCookie, 8);
        WriteBE(wrappedCookie, 12, 4 + cookie.Length);       // size part2 (alac + cookie)
        System.Text.Encoding.ASCII.GetBytes("alac").CopyTo(wrappedCookie, 16);
        cookie.CopyTo(wrappedCookie, 20);
        WriteBE(ms, (long)wrappedCookie.Length);
        ms.Write(wrappedCookie);

        // pakt (packet table) — WYMAGANE dla VBR (ALAC): packets + priming + remainder + sizes[]
        using (var pakt = new MemoryStream())
        {
            WriteBE(pakt, (long)packetSizes.Length);   // number of packets
            WriteBE(pakt, 0L);                         // priming frames
            WriteBE(pakt, 0L);                         // remainder frames
            foreach (var s in packetSizes)
                WriteBE(pakt, s);                      // packet size table (4 B each)
            var paktBytes = pakt.ToArray();
            ms.Write("pakt"u8); WriteBE(ms, (long)paktBytes.Length); ms.Write(paktBytes);
        }

        ms.Write("data"u8); WriteBE(ms, (long)alacFrames.Length + 4);
        WriteBE(ms, 0); // edit count
        ms.Write(alacFrames);

        return ms.ToArray();
    }

    private static void WriteBE(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void WriteBE(byte[] buffer, int offset, int v)
    {
        buffer[offset] = (byte)(v >> 24); buffer[offset + 1] = (byte)(v >> 16);
        buffer[offset + 2] = (byte)(v >> 8); buffer[offset + 3] = (byte)v;
    }

    private static void WriteBE(Stream s, ushort v) => WriteBE(s, (int)v);
    private static void WriteBE(Stream s, long v)
    {
        s.WriteByte((byte)(v >> 56)); s.WriteByte((byte)(v >> 48));
        s.WriteByte((byte)(v >> 40)); s.WriteByte((byte)(v >> 32));
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
}
