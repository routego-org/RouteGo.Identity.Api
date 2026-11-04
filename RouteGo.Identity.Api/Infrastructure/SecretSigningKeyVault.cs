using Amazon.SecretsManager.Model;

namespace RouteGo.Identity.Api.Infrastructure;

/// <summary>
/// Reads signing keys through <see cref="SecretResolver"/>: user-secrets in Development, Secrets
/// Manager everywhere else. Each key is its own secret, named after its key ID, so two keys can be
/// held at once during a rotation and the kid in a token names the exact secret that signed it.
/// </summary>
public sealed class SecretSigningKeyVault : ISigningKeyVault
{
    /// <summary>The secret holding key k-2026-10 is "routego/identity/jwt-signing-key/k-2026-10".</summary>
    public const string SecretNamePrefix = "routego/identity/jwt-signing-key/";

    private readonly SecretResolver _secrets;

    public SecretSigningKeyVault(SecretResolver secrets)
    {
        _secrets = secrets;
    }

    /// <inheritdoc />
    public async Task<string?> GetPrivateKeyPemAsync(string keyId, CancellationToken cancellationToken)
    {
        try
        {
            return await _secrets.ResolveAsync(SecretNamePrefix + keyId);
        }
        catch (ResourceNotFoundException)
        {
            // A key that was never stored is an answer, not a fault; a denied read still throws.
            return null;
        }
    }
}
