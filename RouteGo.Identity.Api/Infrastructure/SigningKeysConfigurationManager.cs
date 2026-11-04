using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Infrastructure;

/// <summary>
/// Hands the JWT bearer handler Identity's own published public keys, read from the signing_keys
/// table rather than fetched from its own /.well-known/jwks.json. Kept for one minute — well inside
/// the ten-minute minimum between introducing and activating a key — and reloaded at once when a
/// token names a key ID the cached set does not hold, exactly as consuming services do.
/// </summary>
public sealed class SigningKeysConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private const string SelectPublishedSql = """
        SELECT key_id, public_key FROM signing_keys.signing_keys WHERE state = ANY(@States)
        """;

    private static readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(1);

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);
    private Snapshot? _snapshot;

    public SigningKeysConfigurationManager(IReaderConnectionFactory readerFactory, IClock clock)
    {
        _readerFactory = readerFactory;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        Snapshot? current = Volatile.Read(ref _snapshot);
        if (current != null && current.LoadedAt + _refreshInterval > _clock.UtcNow)
        {
            return current.Configuration;
        }

        await _loadLock.WaitAsync(cancel);
        try
        {
            return await LoadAsync(cancel);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <inheritdoc />
    public void RequestRefresh()
    {
        Volatile.Write(ref _snapshot, null);
    }

    public void Dispose()
    {
        _loadLock.Dispose();
    }

    private async Task<OpenIdConnectConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        Snapshot? current = Volatile.Read(ref _snapshot);
        if (current != null && current.LoadedAt + _refreshInterval > _clock.UtcNow)
        {
            return current.Configuration;
        }

        await using DbConnection connection = _readerFactory.CreateReadConnection();
        IEnumerable<KeyRow> rows = await connection.QueryAsync<KeyRow>(new CommandDefinition(
            SelectPublishedSql, new { States = SigningKeyStates.Published.ToArray() }, cancellationToken: cancellationToken));

        var configuration = new OpenIdConnectConfiguration();
        foreach (KeyRow row in rows)
        {
            configuration.SigningKeys.Add(ToSecurityKey(row));
        }

        Volatile.Write(ref _snapshot, new Snapshot(configuration, _clock.UtcNow));
        return configuration;
    }

    private static RsaSecurityKey ToSecurityKey(KeyRow row)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(row.PublicKey);
        return new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false)) { KeyId = row.KeyId };
    }

    private sealed record KeyRow(string KeyId, string PublicKey);

    private sealed record Snapshot(OpenIdConnectConfiguration Configuration, DateTimeOffset LoadedAt);
}
