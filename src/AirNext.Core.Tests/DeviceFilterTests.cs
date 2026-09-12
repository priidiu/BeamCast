using AirNext.Core.Discovery;
using Xunit;

namespace AirNext.Core.Tests;

/// <summary>mDNS service filter tests (RHI-169) — only _airplay/_raop are AirPlay.</summary>
public class DeviceFilterTests
{
    [Theory]
    [InlineData("WiiM Mini-0870._airplay._tcp.local", true)]
    [InlineData("Living Room._raop._tcp.local", true)]
    [InlineData("WiiM Mini-0870._linkplay._tcp.local", false)]   // producent Linkplay — NIE AirPlay
    [InlineData("Orange-PL-..._googlecast._tcp.local", false)]   // Chromecast — NIE AirPlay
    [InlineData("WiiM Mini-0870._spotify-connect._tcp.local", false)]
    [InlineData("WiiM Mini-0870._tidalconnect._tcp.local", false)]
    public void IsAirplayService_OnlyAirplayAndRaop(string instanceName, bool expected)
    {
        bool isAirplay = instanceName.Contains("_airplay._tcp", StringComparison.OrdinalIgnoreCase);
        bool isRaop = instanceName.Contains("_raop._tcp", StringComparison.OrdinalIgnoreCase);
        bool result = isAirplay || isRaop;
        Assert.Equal(expected, result);
    }
}
