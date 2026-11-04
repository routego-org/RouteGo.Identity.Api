namespace RouteGo.Identity.Api.Common.Sessions;

/// <summary>A session: the signed access token and the single-use refresh token that renews it.</summary>
public sealed class TokenPair
{
    /// <summary>Signed RS256 access token; its kid names the key that signed it.</summary>
    public required string AccessToken { get; init; }

    public required DateTimeOffset AccessTokenExpiresAt { get; init; }

    /// <summary>Single-use refresh token; only its hash is kept.</summary>
    public required string RefreshToken { get; init; }

    public required DateTimeOffset RefreshTokenExpiresAt { get; init; }

    /// <summary>Always "Bearer".</summary>
    public required string TokenType { get; init; }
}
