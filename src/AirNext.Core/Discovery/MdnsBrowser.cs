using System.Net;
using System.Net.Sockets;

namespace AirNext.Core.Discovery;

/// <summary>
/// mDNS/DNS-SD browser (RHI-160) — zero dependencies, native multicast UDP 224.0.0.251:5353.
/// Discovers AirPlay 1 devices: _airplay._tcp.local (WiiM) + _raop._tcp.local (Apple).
///
/// DUAL-SOCKET architecture (key on Windows — the native mDNS resolver owns 5353):
///  - RX: bind 5353 + join multicast on ALL interfaces → receive multicast answers
///  - TX: bind random port → send query with QU bit (unicast response) → receive unicast answers
///
/// DNS-SD sequence: PTR (find instances) → SRV (host:port) → TXT (features) → A (IP).
/// </summary>
public sealed class MdnsBrowser : IDisposable
{
    public const string MulticastAddress = "224.0.0.251";
    public const int MulticastPort = 5353;
    public const string AirplayService = "_airplay._tcp.local";
    public const string RaopService = "_raop._tcp.local";
    public const string ServicesBrowse = "_services._dns-sd._udp.local"; // service browse (like TuneBlade)

    private readonly UdpClient _rx; // multicast listen (5353)
    private readonly UdpClient _tx; // send query + receive unicast (random port)
    private readonly string _multicastAddress;
    private readonly int _multicastPort;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    private readonly Dictionary<string, AirplayDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AirplayDevice> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Diagnostics — what happened at init (live-hardware debug).</summary>
    public List<string> Diagnostics { get; } = new();

    /// <summary>Discovered devices (complete, after resolve).</summary>
    public IReadOnlyList<AirplayDevice> Devices { get { lock (_lock) return _devices.Values.ToList(); } }

    /// <summary>A new device was found.</summary>
    public event Action<AirplayDevice>? DeviceAdded;

    /// <summary>
    /// Creates an mDNS browser. Default multicast 224.0.0.251:5353.
    /// In tests (CI/Linux without multicast loopback) pass 127.0.0.1:5353 — then
    /// queries go unicast to a local responder (same logic, different transport).
    /// </summary>
    public MdnsBrowser(string multicastAddress = MulticastAddress, int multicastPort = MulticastPort)
    {
        _multicastAddress = multicastAddress;
        _multicastPort = multicastPort;

        var useMulticast = multicastAddress == MulticastAddress;

        _tx = new UdpClient(AddressFamily.InterNetwork);
        _tx.Client.Bind(new IPEndPoint(IPAddress.Any, 0)); // losowy port — TX

        if (useMulticast)
        {
            _rx = new UdpClient(AddressFamily.InterNetwork);
            _rx.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                _rx.Client.Bind(new IPEndPoint(IPAddress.Any, multicastPort));
                Diagnostics.Add($"RX bind 0.0.0.0:{multicastPort} OK");
            }
            catch (Exception ex)
            {
                Diagnostics.Add($"RX bind FAIL: {ex.Message}");
                _rx.Dispose();
                throw;
            }
            _rx.MulticastLoopback = true;

            var mcastGroup = IPAddress.Parse(multicastAddress);
            int joined = 0;
            var ifaces = GetIpv4InterfaceAddresses();
            Diagnostics.Add($"IPv4 interfaces (up, non-loopback): {string.Join(", ", ifaces)}");
            foreach (var local in ifaces)
            {
                try
                {
                    _rx.JoinMulticastGroup(mcastGroup, local);
                    joined++;
                }
                catch (Exception ex)
                {
                    Diagnostics.Add($"join {local} FAIL: {ex.Message}");
                }
            }
            Diagnostics.Add($"joined multicast on {joined} interfaces (IPv4 non-loopback)");

            if (joined == 0)
            {
                try
                {
                    _rx.JoinMulticastGroup(mcastGroup);
                    Diagnostics.Add("join fallback (no interface) OK");
                }
                catch (Exception ex)
                {
                    Diagnostics.Add($"join fallback FAIL: {ex.Message}");
                }
            }
        }
        else
        {
            // Test mode: unicast to 127.0.0.1:port
            _rx = new UdpClient(AddressFamily.InterNetwork);
            _rx.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
    }

    /// <summary>Enumerate IPv4 interface addresses (up, non-loopback) — per-interface multicast join.</summary>
    private static List<IPAddress> GetIpv4InterfaceAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                        result.Add(ua.Address);
                }
            }
        }
        catch (Exception) { /* no permission — fallback */ }
        return result;
    }

    /// <summary>Start browse — send PTR queries on an interval, listen for answers.</summary>
    public void Start(TimeSpan? queryInterval = null)
    {
        var interval = queryInterval ?? TimeSpan.FromSeconds(1);
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);

        // mDNS: no answer = retry (RFC 6762). Aggressive re-query every 1 s:
        //  - _services browse (jak TuneBlade — WiiM/Linkplay odpowiada tylko na to)
        //  - direct PTR _airplay/_raop (some devices answer this)
        //  - re-resolve pending instances (SRV/TXT/A may arrive later)
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    SendQuery(ServicesBrowse);
                    SendQuery(AirplayService);
                    SendQuery(RaopService);

                    // Re-resolve pending instances (SRV/TXT/A often arrive staggered)
                    string[] pendingNames;
                    lock (_lock)
                        pendingNames = _pending.Keys.ToArray();
                    foreach (var name in pendingNames)
                    {
                        SendQueryFor(name, MdnsParser.TypeSrv);
                        SendQueryFor(name, MdnsParser.TypeTxt);
                        SendQueryFor(name, MdnsParser.TypeA);
                    }

                    await Task.Delay(interval, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private void SendQuery(string service)
    {
        var query = MdnsParser.BuildPtrQuery(service);
        SendToAllInterfaces(query);
    }

    /// <summary>
    /// Unicast query straight to a given IP:5353 (RHI-169).
    /// WiiM/Linkplay ma cooldown ~45 s na odpowiedzi multicast (RFC 6762 backoff),
    /// ale unicast query z QU bit (BuildPtrQuery ma QClassUnicastResponse) wymusza
    /// immediate answer — bypasses cooldown. Used when we already know the device IP.
    /// </summary>
    public void SendQueryUnicast(string ip)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out _)) return;
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), MulticastPort);
            foreach (var svc in new[] { AirplayService, RaopService })
            {
                var query = MdnsParser.BuildPtrQuery(svc);
                _tx.Send(query, query.Length, endpoint);
            }
        }
        catch (Exception) { /* ignoruj — unicast best-effort */ }
    }

    private void SendQueryFor(string name, ushort type)
    {
        var query = MdnsParser.BuildQuery(name, type);
        SendToAllInterfaces(query);
    }

    /// <summary>
    /// Send a multicast packet on every IPv4 interface (set MulticastInterface per send).
    /// KEY on Windows with VPN: without MulticastInterface the query uses the default interface
    /// (NordLynx/OpenVPN) i ginie — nie dociera do sieci WiiM.
    /// </summary>
    private void SendToAllInterfaces(byte[] packet)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(_multicastAddress), _multicastPort);

        // Send on every IPv4 interface (non-loopback) — explicit MulticastInterface
        bool anySent = false;
        foreach (var local in GetIpv4InterfaceAddresses())
        {
            try
            {
                SetMulticastInterface(_tx, local);
                _tx.Send(packet, packet.Length, endpoint);
                anySent = true;
            }
            catch (Exception)
            {
                // interface down / no multicast — skip
            }
        }

        // Fallback: send without choosing an interface (default)
        if (!anySent)
        {
            try
            {
                _tx.Send(packet, packet.Length, endpoint);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Ustawia MulticastInterface na sockecie (network-order int z adresu IPv4).</summary>
    private static void SetMulticastInterface(UdpClient client, IPAddress local)
    {
        var addrBytes = local.GetAddressBytes();
        int ifIndex = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(addrBytes, 0));
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ifIndex);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        // Listen on both sockets INDEPENDENTLY (bug: Task.WhenAny ended the loop if one threw).
        // An error on one socket must not kill listen on the other.
        var rxTask = Task.Run(() => ReceiveLoopAsync(_rx, ct), CancellationToken.None);
        var txTask = Task.Run(() => ReceiveLoopAsync(_tx, ct), CancellationToken.None);
        try
        {
            await Task.WhenAll(rxTask, txTask).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // per-packet errors handled inside ReceiveLoopAsync — continue
        }
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _packetsReceivedField);
                ProcessPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException)
            {
                // transient socket error — brief pause and continue (do not abort)
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private long _packetsReceivedField;
    public long PacketsReceived => Interlocked.Read(ref _packetsReceivedField);

    private void ProcessPacket(byte[] packet, IPEndPoint source)
    {
        try
        {
            var msg = MdnsParser.Parse(packet);
            if (!msg.Response)
                return;

            foreach (var r in msg.Answers)
            {
                switch (r.Type)
                {
                    case var t when t == MdnsParser.TypePtr:
                        HandlePtr(r, packet);
                        break;
                    case var t when t == MdnsParser.TypeSrv:
                        HandleSrv(r, packet);
                        break;
                    case var t when t == MdnsParser.TypeTxt:
                        HandleTxt(r);
                        break;
                    case var t when t == MdnsParser.TypeA || t == MdnsParser.TypeAaaa:
                        HandleAddress(r, source);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // bad packet — ignore
        }
    }

    private void HandlePtr(MdnsParser.Record ptr, byte[] packet)
    {
        // PTR rdata = full instance name (e.g. "WiiM Mini._airplay._tcp.local") — may be compressed
        var target = MdnsParser.DecodeNameAtOffset(packet, ptr.DataOffset);
        if (string.IsNullOrEmpty(target))
            return;

        // Answer to browse _services._dns-sd._udp.local: rdata = service name (e.g. _airplay._tcp.local)
        // → send PTR query for that service (find instances)
        if (ptr.Name.Equals(ServicesBrowse, StringComparison.OrdinalIgnoreCase))
        {
            if (target.Contains("_airplay._tcp", StringComparison.OrdinalIgnoreCase) ||
                target.Contains("_raop._tcp", StringComparison.OrdinalIgnoreCase))
            {
                SendQuery(target);
            }
            return;
        }

        // PTR for a concrete service: rdata = instance (e.g. "WiiM Mini-0870._airplay._tcp.local")
        var instanceName = target;

        // FILTER: only _airplay._tcp / _raop._tcp are AirPlay services.
        // _linkplay._tcp (WiiM), _googlecast._tcp (Orange/Chromecast), _spotify-connect itd.
        // those are NOT RAOP/AP1 — skip (otherwise WiiM shows twice: _linkplay + _airplay).
        bool isAirplay = instanceName.Contains("_airplay._tcp", StringComparison.OrdinalIgnoreCase);
        bool isRaop = instanceName.Contains("_raop._tcp", StringComparison.OrdinalIgnoreCase);
        if (!isAirplay && !isRaop)
            return;

        var service = isRaop ? "raop" : "airplay";

        lock (_lock)
        {
            if (_devices.ContainsKey(instanceName) || _pending.ContainsKey(instanceName))
                return;
            _pending[instanceName] = new AirplayDevice
            {
                Name = instanceName.Split('.')[0],
                Host = "",
                Port = 0,
                ServiceType = service,
                FullName = instanceName,
            };
        }

        // Resolve: SRV + TXT + A dla instancji (mDNS resolver odpowie)
        SendQueryFor(instanceName, MdnsParser.TypeSrv);
        SendQueryFor(instanceName, MdnsParser.TypeTxt);
        SendQueryFor(instanceName, MdnsParser.TypeA);
    }

    private void HandleSrv(MdnsParser.Record srv, byte[] packet)
    {
        // SRV: owner = instancja, target = host.local, port
        if (srv.Data.Length < 7)
            return;
        ushort port = (ushort)((srv.Data[4] << 8) | srv.Data[5]);
        var target = MdnsParser.DecodeNameAtOffset(packet, srv.DataOffset + 6);
        if (string.IsNullOrEmpty(target))
            return;

        lock (_lock)
        {
            if (!_pending.TryGetValue(srv.Name, out var dev))
                return;
            dev = dev with { Host = target.Split('.')[0], Port = port };
            _pending[srv.Name] = dev;
        }

        // A query na host
        SendQueryFor(target, MdnsParser.TypeA);
    }

    private void HandleTxt(MdnsParser.Record txt)
    {
        var dict = MdnsParser.ParseTxt(txt.Data);
        lock (_lock)
        {
            if (!_pending.TryGetValue(txt.Name, out var dev))
                return;

            ulong features = 0;
            if (dict.TryGetValue("tp", out var tpHex) &&
                ulong.TryParse(tpHex, System.Globalization.NumberStyles.HexNumber, null, out var tp))
                features = tp;

            var updated = dev with { Txt = dict, FeaturesRaw = features };
            _pending[txt.Name] = updated;
            TryComplete(updated);
        }
    }

    private void HandleAddress(MdnsParser.Record addr, IPEndPoint source)
    {
        string hostName = addr.Name.Split('.')[0];
        string ip = addr.Type == MdnsParser.TypeA && addr.Data.Length == 4
            ? new IPAddress(addr.Data).ToString()
            : addr.Type == MdnsParser.TypeAaaa && addr.Data.Length == 16
                ? new IPAddress(addr.Data).ToString()
                : "";

        lock (_lock)
        {
            foreach (var key in _pending.Keys.ToList())
            {
                var dev = _pending[key];
                if (dev.Host.Equals(hostName, StringComparison.OrdinalIgnoreCase))
                {
                    _pending[key] = dev with { Host = ip };
                    TryComplete(dev with { Host = ip });
                }
            }
        }
    }

    private void TryComplete(AirplayDevice dev)
    {
        if (dev.Port == 0)
            return;
        // Host must be an IP (not the SRV name) — wait for A/AAAA
        if (!IPAddress.TryParse(dev.Host, out _))
            return;

        if (_devices.ContainsKey(dev.FullName))
            return;

        _devices[dev.FullName] = dev;
        _pending.Remove(dev.FullName);
        DeviceAdded?.Invoke(dev);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_multicastAddress == MulticastAddress)
        {
            try { _rx.DropMulticastGroup(IPAddress.Parse(MulticastAddress)); } catch { }
        }
        _rx.Dispose();
        _tx.Dispose();
        _cts.Dispose();
    }
}
