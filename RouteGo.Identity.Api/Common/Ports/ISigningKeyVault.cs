namespace RouteGo.Identity.Api.Common.Ports;

/// <summary>
/// Reads the private half of a signing key from the protected secret store. Only Identity holds
/// this material; it is never stored in the database and never returned by any operation.
/// </summary>
public interface ISigningKeyVault
{
    /// <summary>The key's private half as PEM text, or null when the store holds no such key.</summary>
    /// <param name="keyId">The key ID the secret is stored under.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<string?> GetPrivateKeyPemAsync(string keyId, CancellationToken cancellationToken);
}
