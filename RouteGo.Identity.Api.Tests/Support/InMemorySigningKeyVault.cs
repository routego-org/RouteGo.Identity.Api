using System.Security.Cryptography;
using RouteGo.Identity.Api.Common.Ports;

namespace RouteGo.Identity.Api.Tests.Support;

/// <summary>A secret store holding freshly generated RSA keys, keyed by key ID.</summary>
public sealed class InMemorySigningKeyVault : ISigningKeyVault
{
    private readonly Dictionary<string, string> _privateKeys = new Dictionary<string, string>();

    /// <summary>Generates a 2048-bit RSA key and stores its private half under the key ID.</summary>
    /// <param name="keyId">The key ID to store it under.</param>
    /// <returns>The private key PEM, so a test can compare the derived public key.</returns>
    public string AddNewKey(string keyId)
    {
        using var rsa = RSA.Create(2048);
        string pem = rsa.ExportPkcs8PrivateKeyPem();
        _privateKeys[keyId] = pem;
        return pem;
    }

    /// <summary>Stores arbitrary text under the key ID, for testing unusable secrets.</summary>
    public void Put(string keyId, string value)
    {
        _privateKeys[keyId] = value;
    }

    public Task<string?> GetPrivateKeyPemAsync(string keyId, CancellationToken cancellationToken)
    {
        _privateKeys.TryGetValue(keyId, out string? pem);
        return Task.FromResult(pem);
    }
}
