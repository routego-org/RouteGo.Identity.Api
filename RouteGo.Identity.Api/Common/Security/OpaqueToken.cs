using System.Buffers.Text;
using System.Security.Cryptography;

namespace RouteGo.Identity.Api.Common.Security;

/// <summary>
/// The secrets Identity hands out and must recognise later — verification-link tokens and refresh
/// tokens. Each is 256 random bits; only its SHA-256 hash is stored, so a copy of the database
/// cannot be turned back into working links or sessions.
/// </summary>
public static class OpaqueToken
{
    private const int TokenBytes = 32;

    /// <summary>A new random token, URL-safe so it can travel in a link.</summary>
    public static string Create()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>The one-way hash that is stored and looked up instead of the token.</summary>
    public static string Hash(string token)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
