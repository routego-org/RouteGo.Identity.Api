using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Common.Sessions;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.RefreshSession;

/// <summary>The refresh token to exchange.</summary>
/// <param name="RefreshToken">The single-use refresh token from the last sign-in or refresh.</param>
public sealed record RefreshSessionRequest(string? RefreshToken);

public enum RefreshSessionOutcome
{
    Refreshed,
    TokenInvalid,
    TokenReused,
}

/// <summary>The outcome, the new session on success, and the account for security logging.</summary>
/// <param name="Outcome">How the exchange ended.</param>
/// <param name="Tokens">The new token pair.</param>
/// <param name="UserId">The account whose chain was revoked on reuse.</param>
public sealed record RefreshSessionResult(RefreshSessionOutcome Outcome, TokenPair? Tokens = null, Guid? UserId = null);

public sealed class RefreshSessionValidator : AbstractValidator<RefreshSessionRequest>
{
    public RefreshSessionValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

public sealed class RefreshSessionHandler
{
    private const string AuditAction = "RefreshSession";

    private const string SelectOwnerSql = """
        SELECT u.user_id, u.email
        FROM authentication.refresh_tokens r
        JOIN authentication.users u ON u.user_id = r.user_id
        WHERE r.token_hash = @TokenHash
        """;

    // Database time for expiry, the same clock every token row is compared against.
    private const string SelectTokenForUpdateSql = """
        SELECT r.refresh_token_id, r.chain_id, r.replaced_by, r.revoked_at, r.expires_at <= now() AS is_expired,
               u.user_id, u.email, u.role
        FROM authentication.refresh_tokens r
        JOIN authentication.users u ON u.user_id = r.user_id
        WHERE r.token_hash = @TokenHash
        FOR UPDATE
        """;

    private const string RevokeChainSql = """
        UPDATE authentication.refresh_tokens
        SET revoked_at = now(), revoke_reason = @Reason
        WHERE chain_id = @ChainId AND revoked_at IS NULL
        """;

    private const string MarkReplacedSql = """
        UPDATE authentication.refresh_tokens SET replaced_by = @ReplacedBy WHERE refresh_token_id = @RefreshTokenId
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IActorWriterConnectionFactory _writerFactory;
    private readonly SessionIssuer _sessionIssuer;

    public RefreshSessionHandler(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory, SessionIssuer sessionIssuer)
    {
        _readerFactory = readerFactory;
        _writerFactory = writerFactory;
        _sessionIssuer = sessionIssuer;
    }

    public async Task<RefreshSessionResult> HandleAsync(RefreshSessionRequest request, CancellationToken cancellationToken)
    {
        string tokenHash = OpaqueToken.Hash(request.RefreshToken!);

        // Only to name the actor: the refresh token is the credential, and its account the actor.
        await using DbConnection reader = _readerFactory.CreateReadConnection();
        OwnerRow? owner = await reader.QuerySingleOrDefaultAsync<OwnerRow>(new CommandDefinition(
            SelectOwnerSql, new { TokenHash = tokenHash }, cancellationToken: cancellationToken));
        if (owner is null)
        {
            return new RefreshSessionResult(RefreshSessionOutcome.TokenInvalid);
        }

        var actor = new ActorIdentity(owner.UserId.ToString(), owner.Email, owner.Email);
        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(actor, AuditAction, cancellationToken);
        TokenRow token = await writer.Connection.QuerySingleAsync<TokenRow>(new CommandDefinition(
            SelectTokenForUpdateSql, new { TokenHash = tokenHash }, writer.Transaction, cancellationToken: cancellationToken));

        RefreshSessionResult result = await DecideAsync(writer, token, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return result;
    }

    // Reuse is checked first: a token that was already exchanged means someone copied it, and the
    // whole chain must end even if that token has since expired or been revoked.
    private async Task<RefreshSessionResult> DecideAsync(IWriterConnection writer, TokenRow token, CancellationToken cancellationToken)
    {
        if (token.ReplacedBy.HasValue)
        {
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                RevokeChainSql, new { token.ChainId, Reason = "ReuseDetected" }, writer.Transaction,
                cancellationToken: cancellationToken));
            return new RefreshSessionResult(RefreshSessionOutcome.TokenReused, UserId: token.UserId);
        }

        if (token.RevokedAt.HasValue || token.IsExpired)
        {
            return new RefreshSessionResult(RefreshSessionOutcome.TokenInvalid);
        }

        var subject = new SessionSubject(token.UserId, token.Email, Enum.Parse<AccountRole>(token.Role));
        IssuedSession session = await _sessionIssuer.IssueAsync(writer, subject, token.ChainId, cancellationToken);
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            MarkReplacedSql, new { ReplacedBy = session.RefreshTokenId, token.RefreshTokenId }, writer.Transaction,
            cancellationToken: cancellationToken));
        return new RefreshSessionResult(RefreshSessionOutcome.Refreshed, session.Tokens);
    }

    private sealed record OwnerRow(Guid UserId, string Email);

    private sealed class TokenRow
    {
        public Guid RefreshTokenId { get; init; }

        public Guid ChainId { get; init; }

        public Guid? ReplacedBy { get; init; }

        public DateTime? RevokedAt { get; init; }

        public bool IsExpired { get; init; }

        public Guid UserId { get; init; }

        public string Email { get; init; } = string.Empty;

        public string Role { get; init; } = string.Empty;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Authentication")]
[Route("auth/sessions/refresh")]
public sealed class RefreshSessionController : ControllerBase
{
    private readonly RefreshSessionHandler _handler;
    private readonly RefreshSessionValidator _validator = new RefreshSessionValidator();
    private readonly ILogger<RefreshSessionController> _logger;

    public RefreshSessionController(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory, SessionIssuer sessionIssuer,
        ILogger<RefreshSessionController> logger)
    {
        _handler = new RefreshSessionHandler(readerFactory, writerFactory, sessionIssuer);
        _logger = logger;
    }

    /// <summary>Exchange a refresh token for a new token pair.</summary>
    /// <response code="200">A new access token and refresh token.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">The refresh token cannot be used (REFRESH_TOKEN_INVALID, REFRESH_TOKEN_REUSED).</response>
    [AllowAnonymous] // Public by design: the refresh token is the credential; the access token may have expired.
    [HttpPost]
    [ProducesResponseType(typeof(TokenPair), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RefreshSession(RefreshSessionRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        RefreshSessionResult result = await _handler.HandleAsync(request, cancellationToken);
        switch (result.Outcome)
        {
            case RefreshSessionOutcome.Refreshed:
                return Ok(result.Tokens);
            case RefreshSessionOutcome.TokenInvalid:
                return ProblemResult.RefreshTokenInvalid(this, "The refresh token is unknown, expired or revoked.");
            case RefreshSessionOutcome.TokenReused:
                // Security review: a reused refresh token means it was copied.
                _logger.LogWarning("Refresh token reuse detected for account {UserId}; its token chain was revoked", result.UserId);
                return ProblemResult.RefreshTokenReused(this, "This refresh token was already used; the session has been ended.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
