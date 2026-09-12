using System.Net;
using System.Net.Sockets;
using System.Text;
using AirNext.Core.Discovery;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>
/// Test integracyjny mDNS (RHI-160): mock responder na multicast loopback odpowiada
/// na PTR/SRV/TXT/A → MdnsBrowser wykrywa kompletne device.
/// </summary>
public class MdnsBrowserTests
{
    private const string MulticastAddr = "224.0.0.251";
    private const int Port = 5353;

    [Fact]
    public async Task Browser_DetectsDevice_FromMockResponder()
    {
 // Tryb testowy: unicast 127.0.0.1:5353 zamiast multicast (multicast loopback nie works na CI/Linux)
        using var responder = new MockMdnsResponder();
        using var browser = new MdnsBrowser("127.0.0.1", 5353);

        AirplayDevice? detected = null;
        var tcs = new TaskCompletionSource<AirplayDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.DeviceAdded += d => { detected = d; tcs.TrySetResult(d); };

        responder.Start();
        browser.Start(queryInterval: TimeSpan.FromMilliseconds(500));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        Assert.True(completed == tcs.Task, $"device not found in 10 s (responderResponded={responder.RespondedCount}, browserDevices={browser.Devices.Count})");

        Assert.NotNull(detected);
        Assert.Equal("WiiM Mini", detected!.Name);
        Assert.Equal("airplay", detected.ServiceType);
        Assert.Equal(7000, detected.Port);
        Assert.Equal("127.0.0.1", detected.Host);
        Assert.Equal("AA:BB:CC:DD:EE:FF", detected.DeviceId);
        Assert.Equal("WiiM Mini", detected.Model);
        Assert.True(detected.FeaturesRaw != 0);
    }

 /// <summary>Minimalny responder mDNS: odpowiada na PTR/SRV/TXT/A dla fake WiiM.</summary>
    private sealed class MockMdnsResponder : IDisposable
    {
        private readonly UdpClient _client;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public int RespondedCount { get; private set; }

        public MockMdnsResponder()
        {
            _client = new UdpClient(AddressFamily.InterNetwork);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Loopback, Port));
        }

        public void Start() => _loop = Task.Run(ListenLoopAsync);

        private async Task ListenLoopAsync()
        {
            var buffer = new byte[4096];
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var result = await _client.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    var msg = MdnsParser.Parse(result.Buffer);
                    RespondedCount++;
                    foreach (var q in msg.Questions)
                    {
                        if (q.Type == MdnsParser.TypePtr && q.Name.Contains("_airplay._tcp", StringComparison.OrdinalIgnoreCase))
                            RespondPtr(result.RemoteEndPoint);
                        else if (q.Type == MdnsParser.TypeSrv && q.Name.StartsWith("WiiM Mini", StringComparison.OrdinalIgnoreCase))
                            RespondSrv(result.RemoteEndPoint);
                        else if (q.Type == MdnsParser.TypeTxt && q.Name.StartsWith("WiiM Mini", StringComparison.OrdinalIgnoreCase))
                            RespondTxt(result.RemoteEndPoint);
                        else if (q.Type == MdnsParser.TypeA && q.Name.StartsWith("wiim", StringComparison.OrdinalIgnoreCase))
                            RespondA(result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
            }
        }

        private void RespondPtr(IPEndPoint target)
        {
            var instance = EncodeName("WiiM Mini._airplay._tcp.local");
            using var ms = new MemoryStream();
            WriteU16(ms, 0); WriteU16(ms, 0x8400);
            WriteU16(ms, 0); WriteU16(ms, 1); WriteU16(ms, 0); WriteU16(ms, 0);
 ms.Write(instance); // answer name = full nazwa instancji (klucz _pending)
            WriteU16(ms, MdnsParser.TypePtr);
            WriteU16(ms, MdnsParser.QClassIn);
            WriteU32(ms, 4500);
            WriteU16(ms, (ushort)instance.Length);
 ms.Write(instance); // rdata = full nazwa instancji
            _client.Send(ms.ToArray(), (int)ms.Length, target);
        }

        private void RespondSrv(IPEndPoint target)
        {
            var instance = EncodeName("WiiM Mini._airplay._tcp.local");
            var host = EncodeName("wiim-mini.local");
            using var ms = new MemoryStream();
            WriteU16(ms, 0); WriteU16(ms, 0x8400);
            WriteU16(ms, 0); WriteU16(ms, 1); WriteU16(ms, 0); WriteU16(ms, 0);
            ms.Write(instance);
            WriteU16(ms, MdnsParser.TypeSrv);
            WriteU16(ms, MdnsParser.QClassIn);
            WriteU32(ms, 120);
            WriteU16(ms, (ushort)(6 + host.Length));
            WriteU16(ms, 0); WriteU16(ms, 0); WriteU16(ms, 7000);
            ms.Write(host);
            _client.Send(ms.ToArray(), (int)ms.Length, target);
        }

        private void RespondTxt(IPEndPoint target)
        {
            var instance = EncodeName("WiiM Mini._airplay._tcp.local");
            byte[] txt =
            {
                9, (byte)'t', (byte)'x', (byte)'t', (byte)'v', (byte)'e', (byte)'r', (byte)'s', (byte)'=', (byte)'1',
                20, (byte)'c', (byte)'n', (byte)'=', (byte)'A', (byte)'A', (byte)':', (byte)'B', (byte)'B', (byte)':',
                (byte)'C', (byte)'C', (byte)':', (byte)'D', (byte)'D', (byte)':', (byte)'E', (byte)'E', (byte)':',
                (byte)'F', (byte)'F',
                12, (byte)'a', (byte)'m', (byte)'=', (byte)'W', (byte)'i', (byte)'i', (byte)'M',
                (byte)' ', (byte)'M', (byte)'i', (byte)'n', (byte)'i',
                4, (byte)'t', (byte)'p', (byte)'=', (byte)'4', (byte)'4',
                3, (byte)'v', (byte)'v', (byte)'=', (byte)'4',
            };
            using var ms = new MemoryStream();
            WriteU16(ms, 0); WriteU16(ms, 0x8400);
            WriteU16(ms, 0); WriteU16(ms, 1); WriteU16(ms, 0); WriteU16(ms, 0);
            ms.Write(instance);
            WriteU16(ms, MdnsParser.TypeTxt);
            WriteU16(ms, MdnsParser.QClassIn);
            WriteU32(ms, 4500);
            WriteU16(ms, (ushort)txt.Length);
            ms.Write(txt);
            _client.Send(ms.ToArray(), (int)ms.Length, target);
        }

        private void RespondA(IPEndPoint target)
        {
            var host = EncodeName("wiim-mini.local");
            using var ms = new MemoryStream();
            WriteU16(ms, 0); WriteU16(ms, 0x8400);
            WriteU16(ms, 0); WriteU16(ms, 1); WriteU16(ms, 0); WriteU16(ms, 0);
            ms.Write(host);
            WriteU16(ms, MdnsParser.TypeA);
            WriteU16(ms, MdnsParser.QClassIn);
            WriteU32(ms, 120);
            WriteU16(ms, 4);
            ms.Write(new byte[] { 127, 0, 0, 1 });
            _client.Send(ms.ToArray(), (int)ms.Length, target);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _client.DropMulticastGroup(IPAddress.Parse(MulticastAddr)); } catch { }
            _client.Dispose();
            _cts.Dispose();
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
}
