using System.Security.Cryptography;
using System.Text;

namespace AirNext.Core.Raop;

/// <summary>
/// Digest auth AirPlay 1 (RHI-144) — wierny port TuneBlade Crypto.ComputeDigest:
///   response = MD5( MD5(user:realm:pass) : nonce : MD5(method:uri) )
/// (bez qop, bez cnonce — AirPlay 1 uses prostego digestu)
/// </summary>
public static class DigestAuth
{
    public static string ComputeResponse(string password, string username, string realm, string nonce, string method, string uri, bool upperCase = false)
    {
        var ha1 = Md5Hex($"{username}:{realm}:{password}");
        var ha2 = Md5Hex($"{method}:{uri}");
        var response = Md5Hex($"{ha1}:{nonce}:{ha2}");
        return upperCase ? response.ToUpperInvariant() : response.ToLowerInvariant();
    }

 /// <summary>Buduje header Authorization: Digest ...</summary>
    public static string BuildAuthorizationHeader(string password, string username, string realm, string nonce, string method, string uri, bool upperCase = false)
    {
        var response = ComputeResponse(password, username, realm, nonce, method, uri, upperCase);
        return $"Digest username=\"{username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"";
    }

    private static string Md5Hex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
