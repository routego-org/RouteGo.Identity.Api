using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Common.Sessions;

/// <summary>A session just issued, and the refresh token row that carries it.</summary>
/// <param name="Tokens">The token pair returned to the caller.</param>
/// <param name="RefreshTokenId">The new refresh token's row, so a rotation can point the old one at it.</param>
public sealed record IssuedSession(TokenPair Tokens, Guid RefreshTokenId);

/// <summary>
/// Starts or continues a session the one way sign-in, refresh and email verification all do: an
/// RS256 access token signed with the active key, and a single-use refresh token of which only the
/// hash is stored. Runs inside the caller's write transaction, so a session exists only when the
/// change that granted it commits.
/// </summary>
public sealed class SessionIssuer
{
    private const string BearerTokenType = "Bearer";

    private const string SelectActiveKeySql = "SELECT key_id FROM signing_keys.signing_keys WHERE state = @State";

    private const string InsertRefreshTokenSql = """
        INSERT INTO authentication.refresh_tokens (user_id, chain_id, token_hash, issued_at, expires_at)
        VALUES (@UserId, @ChainId, @TokenHash, @IssuedAt, @ExpiresAt)
        RETURNING refresh_token_id
        """;

    private static readonly JsonWebTokenHandler _tokenHandler = new JsonWebTokenHandler();

    private readonly ISigningKeyVault _vault;
    private readonly IClock _clock;
    private readonly SessionSettings _settings;

    public SessionIssuer(ISigningKeyVault vault, IClock clock, SessionSettings settings)
    {
        _vault = vault;
        _clock = clock;
        _settings = settings;
    }

    /// <summary>Issues an access token and a refresh token in the given chain.</summary>
    /// <param name="writer">The caller's open write transaction.</param>
    /// <param name="subject">The account the session belongs to.</param>
    /// <param name="chainId">The token chain: new for a sign-in, inherited on a refresh.</param>
    /// <param name="cancellationToken">Cancels the reads and the insert.</param>
    public async Task<IssuedSession> IssueAsync(
        IWriterConnection writer, SessionSubject subject, Guid chainId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset accessExpiresAt = now + _settings.AccessTokenLifetime;
        string accessToken = await CreateAccessTokenAsync(writer, subject, now, cancellationToken);

        string refreshToken = OpaqueToken.Create();
        DateTimeOffset refreshExpiresAt = now + _settings.RefreshTokenLifetime;
        var parameters = new
        {
            subject.UserId,
            ChainId = chainId,
            TokenHash = OpaqueToken.Hash(refreshToken),
            IssuedAt = now,
            ExpiresAt = refreshExpiresAt,
        };
        Guid refreshTokenId = await writer.Connection.QuerySingleAsync<Guid>(new CommandDefinition(
            InsertRefreshTokenSql, parameters, writer.Transaction, cancellationToken: cancellationToken));

        var tokens = new TokenPair
        {
            AccessToken = accessToken,
            AccessTokenExpiresAt = accessExpiresAt,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = refreshExpiresAt,
            TokenType = BearerTokenType,
        };
        return new IssuedSession(tokens, refreshTokenId);
    }

    private async Task<string> CreateAccessTokenAsync(
        IWriterConnection writer, SessionSubject subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? keyId = await writer.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            SelectActiveKeySql, new { State = SigningKeyStates.Active }, writer.Transaction,
            cancellationToken: cancellationToken));
        if (keyId is null)
        {
            throw new InvalidOperationException("No signing key is active; run rotate-key introduce and activate.");
        }

        string? privateKeyPem = await _vault.GetPrivateKeyPemAsync(keyId, cancellationToken);
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException($"The private half of active signing key '{keyId}' cannot be read.");
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + _settings.AccessTokenLifetime).UtcDateTime,
            Claims = BuildClaims(subject),
            SigningCredentials = CreateSigningCredentials(rsa, keyId),
        };
        return _tokenHandler.CreateToken(descriptor);
    }

    // The claim names and role spelling are the ones every RouteGo service reads (architecture/tokens.md).
    private static Dictionary<string, object> BuildClaims(SessionSubject subject)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject.UserId.ToString(),
            ["email"] = subject.Email,
            ["role"] = subject.Role.ToString(),
        };
        if (subject.Role == AccountRole.Operator)
        {
            claims["operatorId"] = subject.UserId.ToString();
        }

        return claims;
    }

    // The RSA instance is disposed as soon as the token is signed, so the signature provider must
    // not be cached against it.
    private static SigningCredentials CreateSigningCredentials(RSA rsa, string keyId)
    {
        var key = new RsaSecurityKey(rsa)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        return new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
    }
}
