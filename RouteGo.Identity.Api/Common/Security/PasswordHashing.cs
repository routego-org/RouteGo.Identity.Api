using Microsoft.AspNetCore.Identity;

namespace RouteGo.Identity.Api.Common.Security;

/// <summary>
/// One-way password hashing with the framework's PBKDF2 hasher. Only the hash is ever stored; the
/// hash carries its own salt and iteration count, so stored hashes stay verifiable when the
/// framework raises its defaults.
/// </summary>
public static class PasswordHashing
{
    private static readonly PasswordHasher<object> _hasher = new PasswordHasher<object>();
    private static readonly object _noUser = new object();

    /// <summary>Hashes a password for storage.</summary>
    public static string Hash(string password)
    {
        return _hasher.HashPassword(_noUser, password);
    }

    /// <summary>Whether the password matches the stored hash.</summary>
    public static bool Verify(string passwordHash, string password)
    {
        PasswordVerificationResult result = _hasher.VerifyHashedPassword(_noUser, passwordHash, password);
        return result != PasswordVerificationResult.Failed;
    }
}
