namespace AirNext.Core.Discovery;

/// <summary>
/// AirPlay device discovered via mDNS (RHI-161) — model for the receiver picker UI (M4).
/// Record (immutable) — aktualizacje przez `with` w MdnsBrowser.
/// </summary>
public sealed record AirplayDevice
{
    /// <summary>Nazwa instancji mDNS (np. "WiiM Mini").</summary>
    public required string Name { get; init; }

    /// <summary>Receiver IP address.</summary>
    public required string Host { get; init; }

    /// <summary>Port RTSP (zwykle 7000 dla _airplay, 5000 dla _raop).</summary>
    public required int Port { get; init; }

 /// <summary>Typ service: "airplay" (_airplay._tcp) lub "raop" (_raop._tcp).</summary>
    public required string ServiceType { get; init; }

 /// <summary>Full nazwa instancji (np. "WiiM Mini._airplay._tcp.local").</summary>
    public required string FullName { get; init; }

    // --- TXT (surowe + sparsowane) ---
    public IReadOnlyDictionary<string, string> Txt { get; init; } = new Dictionary<string, string>();

    /// <summary>Device ID z TXT `cn` (np. "AA:BB:CC:DD:EE:FF").</summary>
    public string? DeviceId => Txt.GetValueOrDefault("cn");

    /// <summary>Model z TXT `am` (np. "WiiM Mini").</summary>
    public string? Model => Txt.GetValueOrDefault("am");

    /// <summary>Wersja AirPlay z TXT `vv`.</summary>
    public string? Version => Txt.GetValueOrDefault("vv");

    /// <summary>Feature bits z TXT `tp` (hex).</summary>
    public ulong FeaturesRaw { get; init; }

    /// <summary>Requires RSA-AES encryption (bit 5 in tp — AirPlay Auth).</summary>
    public bool RequiresEncryption => (FeaturesRaw & (1UL << 5)) != 0;

    /// <summary>Wspiera AirPlay 2 (bit 31 w tp = supportsAP2? — patrz ft).</summary>
    public bool SupportsAirPlay2 { get; init; }

    public override string ToString() =>
        $"{Name} ({Host}:{Port}, {ServiceType}, features=0x{FeaturesRaw:X})";
}
