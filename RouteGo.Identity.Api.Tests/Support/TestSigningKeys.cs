using System.Security.Cryptography;
using RouteGo.Identity.Api.Tests.Database;

namespace RouteGo.Identity.Api.Tests.Support;

/// <summary>Makes a real RSA key the active signing key: its private half in the vault, its public half in the database.</summary>
public static class TestSigningKeys
{
    /// <summary>The key ID tests sign with.</summary>
    public const string ActiveKeyId = "k-test";

    /// <summary>Seeds the active key and returns its public half as PEM, for validating issued tokens.</summary>
    public static async Task<string> SeedActiveAsync(DatabaseFixture database, InMemorySigningKeyVault vault)
    {
        string privateKeyPem = vault.AddNewKey(ActiveKeyId);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        string publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        await database.SeedAsync(
            """
            INSERT INTO signing_keys.signing_keys (key_id, public_key, state, introduced_at, introduced_by, activated_at, activated_by)
            VALUES (@KeyId, @PublicKey, 'Active', now(), 'instructor', now(), 'instructor')
            """,
            new { KeyId = ActiveKeyId, PublicKey = publicKeyPem });
        return publicKeyPem;
    }
}
