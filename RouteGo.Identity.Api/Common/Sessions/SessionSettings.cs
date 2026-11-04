namespace RouteGo.Identity.Api.Common.Sessions;

/// <summary>What every access token says about itself and how long each token lives.</summary>
public sealed class SessionSettings
{
    /// <summary>The iss claim.</summary>
    public required string Issuer { get; init; }

    /// <summary>The aud claim.</summary>
    public required string Audience { get; init; }

    /// <summary>How long an access token is valid (default 15 minutes).</summary>
    public required TimeSpan AccessTokenLifetime { get; init; }

    /// <summary>How long a refresh token is valid (default 7 days).</summary>
    public required TimeSpan RefreshTokenLifetime { get; init; }
}
