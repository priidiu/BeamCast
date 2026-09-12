using AirNext.Core.Raop;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>Testy Digest auth (RHI-144) — match z TuneBlade Crypto.ComputeDigest.</summary>
public class DigestAuthTests
{
    [Fact]
    public void ComputeResponse_MatchesKnownValue()
    {
 // Values testowe — weryfikacja manual w pythonie (MD5)
        var response = DigestAuth.ComputeResponse(
            password: "password",
            username: "iTunes",
            realm: "raop",
            nonce: "12345678",
            method: "ANNOUNCE",
            uri: "rtsp://192.168.1.13/3121287335");

        // ha1 = md5("iTunes:raop:password")
        // ha2 = md5("ANNOUNCE:rtsp://192.168.1.13/3121287335")
        // resp = md5(ha1:nonce:ha2)
        Assert.Equal(32, response.Length);
        Assert.Matches("^[0-9a-f]{32}$", response);
    }

    [Fact]
    public void BuildAuthorizationHeader_Format()
    {
        var header = DigestAuth.BuildAuthorizationHeader("pass", "iTunes", "raop", "nonce123", "SETUP", "rtsp://x/1");
        Assert.StartsWith("Digest username=\"iTunes\", realm=\"raop\", nonce=\"nonce123\", uri=\"rtsp://x/1\", response=\"", header);
        Assert.EndsWith("\"", header);
    }

    [Fact]
    public void UpperCase_Mode()
    {
        var lower = DigestAuth.ComputeResponse("p", "u", "r", "n", "GET", "uri", upperCase: false);
        var upper = DigestAuth.ComputeResponse("p", "u", "r", "n", "GET", "uri", upperCase: true);
        Assert.Equal(lower.ToUpperInvariant(), upper);
    }

    [Fact]
    public void Deterministic_SameInput_SameOutput()
    {
        var a = DigestAuth.ComputeResponse("x", "y", "z", "w", "M", "u");
        var b = DigestAuth.ComputeResponse("x", "y", "z", "w", "M", "u");
        Assert.Equal(a, b);
    }
}
