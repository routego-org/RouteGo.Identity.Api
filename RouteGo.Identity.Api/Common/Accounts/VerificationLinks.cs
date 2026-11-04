using RouteGo.Identity.Api.Common.Security;

namespace RouteGo.Identity.Api.Common.Accounts;

/// <summary>A verification link just issued: the link to send and when it stops working.</summary>
/// <param name="Link">The link, carrying the raw single-use token.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
public sealed record IssuedVerificationLink(string Link, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues verification links the one way every flow uses — registration, operator application and
/// resend: a fresh random token, only its hash stored, a link to the Portal's /auth/verify page,
/// valid for the configured lifetime.
/// </summary>
public sealed class VerificationLinks
{
    // The Portal's verify page, as its own spec names it (RouteGo.App, email-verification CAP-UI-AUTH-002).
    private const string VerificationPagePath = "/auth/verify?token=";

    // Database time: verification, expiry and the resend limits all compare against the same clock.
    private const string InsertTokenSql = """
        INSERT INTO authentication.email_verification_tokens (user_id, token_hash, issued_at, expires_at)
        VALUES (@UserId, @TokenHash, now(), now() + @Lifetime)
        RETURNING expires_at
        """;

    private readonly VerificationLinkSettings _settings;

    public VerificationLinks(VerificationLinkSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Stores a new link's token hash in the open write transaction and returns the link.</summary>
    /// <param name="writer">The caller's open write transaction.</param>
    /// <param name="userId">The account the link verifies.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    public async Task<IssuedVerificationLink> IssueAsync(
        IWriterConnection writer, Guid userId, CancellationToken cancellationToken)
    {
        string token = OpaqueToken.Create();
        var parameters = new { UserId = userId, TokenHash = OpaqueToken.Hash(token), _settings.Lifetime };
        DateTime expiresAt = await writer.Connection.QuerySingleAsync<DateTime>(new CommandDefinition(
            InsertTokenSql, parameters, writer.Transaction, cancellationToken: cancellationToken));

        string link = _settings.PortalBaseUrl + VerificationPagePath + token;
        return new IssuedVerificationLink(link, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)));
    }
}
