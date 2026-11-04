using Microsoft.Extensions.Diagnostics.HealthChecks;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Infrastructure;

/// <summary>
/// Readiness: Identity is ready only when it can sign tokens — one key is active and its private
/// half can be read from the secret store. Until the first key is activated, sign-in cannot work,
/// so the instance must not receive traffic.
/// </summary>
public sealed class SigningKeyHealthCheck : IHealthCheck
{
    private const string SelectActiveSql = "SELECT key_id FROM signing_keys.signing_keys WHERE state = @State";

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly ISigningKeyVault _vault;

    public SigningKeyHealthCheck(IReaderConnectionFactory readerFactory, ISigningKeyVault vault)
    {
        _readerFactory = readerFactory;
        _vault = vault;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = _readerFactory.CreateReadConnection();
        string? activeKeyId = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            SelectActiveSql, new { State = SigningKeyStates.Active }, cancellationToken: cancellationToken));
        if (activeKeyId is null)
        {
            return HealthCheckResult.Unhealthy("No signing key is active; run rotate-key introduce and activate.");
        }

        string? privateKeyPem = await _vault.GetPrivateKeyPemAsync(activeKeyId, cancellationToken);
        SigningKeyReading reading = SigningKeyMaterial.Read(privateKeyPem);
        if (reading.Reason != null)
        {
            return HealthCheckResult.Unhealthy($"Active signing key '{activeKeyId}' cannot sign: {reading.Reason}.");
        }

        return HealthCheckResult.Healthy();
    }
}
