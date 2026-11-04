using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Common.Sessions;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.VerifyEmail;

/// <summary>The token carried by a verification link.</summary>
/// <param name="Token">The single-use token from the link.</param>
public sealed record VerifyEmailRequest(string? Token);

/// <summary>The verified account, signed in when it is a customer.</summary>
public sealed class VerifyEmailResponse
{
    public required Guid UserId { get; init; }

    public required AccountRole Role { get; init; }

    public required AccountStatus Status { get; init; }

    /// <summary>A fresh session for a customer; null for an operator, who signs in only after approval.</summary>
    public required TokenPair? Tokens { get; init; }
}

public enum VerifyEmailOutcome
{
    Verified,
    LinkExpired,
    LinkAlreadyUsed,
    LinkInvalid,
}

/// <summary>The outcome, the verified account on success, and the account status a used link reports.</summary>
/// <param name="Outcome">How verification ended.</param>
/// <param name="Response">The verified account.</param>
/// <param name="AccountStatus">Where the account is now, for an already-used link.</param>
public sealed record VerifyEmailResult(
    VerifyEmailOutcome Outcome, VerifyEmailResponse? Response = null, AccountStatus? AccountStatus = null);

public sealed class VerifyEmailValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}

public sealed class VerifyEmailHandler
{
    private const string AuditAction = "VerifyEmail";

    private const string SelectLinkOwnerSql = """
        SELECT u.user_id, u.email
        FROM authentication.email_verification_tokens t
        JOIN authentication.users u ON u.user_id = t.user_id
        WHERE t.token_hash = @TokenHash
        """;

    // Database time, so "expired" and "verified now" agree with the stored issue and expiry times.
    private const string SelectLinkForUpdateSql = """
        SELECT t.token_id, t.used_at, t.voided_at, t.expires_at <= now() AS is_expired,
               u.user_id, u.email, u.role, u.status
        FROM authentication.email_verification_tokens t
        JOIN authentication.users u ON u.user_id = t.user_id
        WHERE t.token_hash = @TokenHash
        FOR UPDATE
        """;

    private const string MarkUsedSql = """
        UPDATE authentication.email_verification_tokens SET used_at = now() WHERE token_id = @TokenId
        """;

    // email_verified_at is set exactly once: the IS NULL guard keeps a second write from moving it.
    private const string VerifyUserSql = """
        UPDATE authentication.users
        SET status = @Status, email_verified_at = now()
        WHERE user_id = @UserId AND email_verified_at IS NULL
        """;

    // The operator's first application waits for the email exactly as the account does, and moves
    // to review with it.
    private const string SubmitApplicationForReviewSql = """
        UPDATE operator_onboarding.operator_applications
        SET status = 'PendingApproval'
        WHERE operator_id = @UserId AND status = 'PendingVerification'
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IActorWriterConnectionFactory _writerFactory;
    private readonly SessionIssuer _sessionIssuer;

    public VerifyEmailHandler(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory, SessionIssuer sessionIssuer)
    {
        _readerFactory = readerFactory;
        _writerFactory = writerFactory;
        _sessionIssuer = sessionIssuer;
    }

    public async Task<VerifyEmailResult> HandleAsync(VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        string tokenHash = OpaqueToken.Hash(request.Token!);

        // Only to name the actor: nobody is signed in, so the account the link belongs to is the
        // actor. Every decision below re-reads the link inside the write transaction.
        await using DbConnection reader = _readerFactory.CreateReadConnection();
        LinkOwnerRow? owner = await reader.QuerySingleOrDefaultAsync<LinkOwnerRow>(new CommandDefinition(
            SelectLinkOwnerSql, new { TokenHash = tokenHash }, cancellationToken: cancellationToken));
        if (owner is null)
        {
            return new VerifyEmailResult(VerifyEmailOutcome.LinkInvalid);
        }

        var actor = new ActorIdentity(owner.UserId.ToString(), owner.Email, owner.Email);
        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(actor, AuditAction, cancellationToken);
        LinkRow link = await writer.Connection.QuerySingleAsync<LinkRow>(new CommandDefinition(
            SelectLinkForUpdateSql, new { TokenHash = tokenHash }, writer.Transaction, cancellationToken: cancellationToken));

        VerifyEmailResult? refusal = Refuse(link);
        if (refusal != null)
        {
            return refusal;
        }

        VerifyEmailResponse response = await VerifyAsync(writer, link, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new VerifyEmailResult(VerifyEmailOutcome.Verified, response);
    }

    // A used link is reported before a voided or expired one: the person's email is verified,
    // which is the most useful thing to tell them.
    private static VerifyEmailResult? Refuse(LinkRow link)
    {
        if (link.UsedAt != null)
        {
            return new VerifyEmailResult(VerifyEmailOutcome.LinkAlreadyUsed, AccountStatus: Enum.Parse<AccountStatus>(link.Status));
        }

        if (link.VoidedAt != null)
        {
            return new VerifyEmailResult(VerifyEmailOutcome.LinkInvalid);
        }

        if (link.IsExpired)
        {
            return new VerifyEmailResult(VerifyEmailOutcome.LinkExpired);
        }

        return null;
    }

    // A verified customer is active and signed in at once; a verified operator waits for review.
    private async Task<VerifyEmailResponse> VerifyAsync(IWriterConnection writer, LinkRow link, CancellationToken cancellationToken)
    {
        AccountRole role = Enum.Parse<AccountRole>(link.Role);
        AccountStatus status = role == AccountRole.Customer ? AccountStatus.Active : AccountStatus.PendingApproval;

        await writer.Connection.ExecuteAsync(new CommandDefinition(
            MarkUsedSql, new { link.TokenId }, writer.Transaction, cancellationToken: cancellationToken));
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            VerifyUserSql, new { link.UserId, Status = status.ToString() }, writer.Transaction,
            cancellationToken: cancellationToken));

        if (role == AccountRole.Operator)
        {
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                SubmitApplicationForReviewSql, new { link.UserId }, writer.Transaction, cancellationToken: cancellationToken));
        }

        TokenPair? tokens = null;
        if (role == AccountRole.Customer)
        {
            var subject = new SessionSubject(link.UserId, link.Email, role);
            IssuedSession session = await _sessionIssuer.IssueAsync(writer, subject, Guid.NewGuid(), cancellationToken);
            tokens = session.Tokens;
        }

        return new VerifyEmailResponse { UserId = link.UserId, Role = role, Status = status, Tokens = tokens };
    }

    private sealed record LinkOwnerRow(Guid UserId, string Email);

    private sealed class LinkRow
    {
        public Guid TokenId { get; init; }

        public DateTime? UsedAt { get; init; }

        public DateTime? VoidedAt { get; init; }

        public bool IsExpired { get; init; }

        public Guid UserId { get; init; }

        public string Email { get; init; } = string.Empty;

        public string Role { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Authentication")]
[Route("auth/emailVerifications")]
public sealed class VerifyEmailController : ControllerBase
{
    private readonly VerifyEmailHandler _handler;
    private readonly VerifyEmailValidator _validator = new VerifyEmailValidator();
    private readonly ILogger<VerifyEmailController> _logger;

    public VerifyEmailController(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory, SessionIssuer sessionIssuer,
        ILogger<VerifyEmailController> logger)
    {
        _handler = new VerifyEmailHandler(readerFactory, writerFactory, sessionIssuer);
        _logger = logger;
    }

    /// <summary>Verify an email address with the token from a verification link.</summary>
    /// <response code="200">The email is verified; a customer is signed in.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="422">The link cannot be used (VERIFICATION_LINK_EXPIRED, VERIFICATION_LINK_ALREADY_USED, VERIFICATION_LINK_INVALID).</response>
    [AllowAnonymous] // Public by design: the link's token is the credential; nobody is signed in yet.
    [HttpPost]
    [ProducesResponseType(typeof(VerifyEmailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        VerifyEmailResult result = await _handler.HandleAsync(request, cancellationToken);
        if (result.Outcome != VerifyEmailOutcome.Verified)
        {
            _logger.LogWarning("Email verification refused: {Outcome}", result.Outcome);
        }

        switch (result.Outcome)
        {
            case VerifyEmailOutcome.Verified:
                return Ok(result.Response);
            case VerifyEmailOutcome.LinkExpired:
                return ProblemResult.VerificationLinkExpired(this, "The link is more than 24 hours old; ask for a new one.");
            case VerifyEmailOutcome.LinkAlreadyUsed:
                string accountStatus = JsonNamingPolicy.CamelCase.ConvertName(result.AccountStatus!.Value.ToString());
                return ProblemResult.VerificationLinkAlreadyUsed(this, "This link was already used.", accountStatus);
            case VerifyEmailOutcome.LinkInvalid:
                return ProblemResult.VerificationLinkInvalid(this, "The link is unknown or was replaced by a newer one.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
