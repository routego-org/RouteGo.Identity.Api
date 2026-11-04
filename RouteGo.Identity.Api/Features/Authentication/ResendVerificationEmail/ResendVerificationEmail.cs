using RouteGo.Identity.Api.Common.Accounts;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.ResendVerificationEmail;

/// <summary>The email to send a fresh verification link to.</summary>
/// <param name="Email">The account's email; compared case-insensitively.</param>
public sealed record ResendVerificationEmailRequest(string? Email);

/// <summary>The email the new link was sent for, and when it stops working.</summary>
/// <param name="Email">The account's email.</param>
/// <param name="LinkExpiresAt">When the new link stops working.</param>
public sealed record ResendVerificationEmailResponse(string Email, DateTimeOffset LinkExpiresAt);

public enum ResendVerificationEmailOutcome
{
    Sent,
    EmailNotRegistered,
    EmailAlreadyVerified,
    RateLimited,
}

/// <summary>The outcome, with what the controller reports for each.</summary>
public sealed class ResendVerificationEmailResult
{
    public required ResendVerificationEmailOutcome Outcome { get; init; }

    public ResendVerificationEmailResponse? Response { get; init; }

    /// <summary>Where the account is, when its email is already verified.</summary>
    public AccountStatus? AccountStatus { get; init; }

    /// <summary>How long until another resend is allowed, when rate limited.</summary>
    public TimeSpan RetryAfter { get; init; }

    /// <summary>The account, for security logging; null when no account uses the email.</summary>
    public Guid? UserId { get; init; }
}

public sealed class ResendVerificationEmailValidator : AbstractValidator<ResendVerificationEmailRequest>
{
    public ResendVerificationEmailValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(AccountRules.MaxEmailLength);
    }
}

public sealed class ResendVerificationEmailHandler
{
    private const string AuditAction = "ResendVerificationEmail";

    private const string SelectAccountSql = """
        SELECT user_id, email FROM authentication.users WHERE lower(email) = lower(@Email)
        """;

    // The row lock serializes concurrent resends for one account, so both cannot pass the limit.
    private const string SelectAccountForUpdateSql = """
        SELECT user_id, email, full_name, role, status FROM authentication.users WHERE user_id = @UserId FOR UPDATE
        """;

    // The account's first link was issued at registration or application and is not a resend.
    private const string SelectRecentResendsSql = """
        SELECT issued_at
        FROM authentication.email_verification_tokens
        WHERE user_id = @UserId
          AND issued_at > @Since
          AND token_id <> (SELECT token_id FROM authentication.email_verification_tokens
                           WHERE user_id = @UserId ORDER BY issued_at, token_id LIMIT 1)
        ORDER BY issued_at
        """;

    private const string VoidUnusedLinksSql = """
        UPDATE authentication.email_verification_tokens
        SET voided_at = now()
        WHERE user_id = @UserId AND used_at IS NULL AND voided_at IS NULL
        """;

    // Database time, the clock the stored issue times were written with.
    private const string SelectNowSql = "SELECT now()";

    private static readonly TimeSpan _limitWindow = TimeSpan.FromHours(24);

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IActorWriterConnectionFactory _writerFactory;
    private readonly VerificationLinks _verificationLinks;

    public ResendVerificationEmailHandler(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory, VerificationLinks verificationLinks)
    {
        _readerFactory = readerFactory;
        _writerFactory = writerFactory;
        _verificationLinks = verificationLinks;
    }

    public async Task<ResendVerificationEmailResult> HandleAsync(
        ResendVerificationEmailRequest request, string correlationId, CancellationToken cancellationToken)
    {
        // Only to name the actor; the decision re-reads the account under a lock below.
        await using DbConnection reader = _readerFactory.CreateReadConnection();
        AccountKey? key = await reader.QuerySingleOrDefaultAsync<AccountKey>(new CommandDefinition(
            SelectAccountSql, new { request.Email }, cancellationToken: cancellationToken));
        if (key is null)
        {
            return new ResendVerificationEmailResult { Outcome = ResendVerificationEmailOutcome.EmailNotRegistered };
        }

        var actor = new ActorIdentity(key.UserId.ToString(), key.Email, key.Email);
        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(actor, AuditAction, cancellationToken);
        AccountRow account = await writer.Connection.QuerySingleAsync<AccountRow>(new CommandDefinition(
            SelectAccountForUpdateSql, new { key.UserId }, writer.Transaction, cancellationToken: cancellationToken));

        ResendVerificationEmailResult? refusal = await RefuseAsync(writer, account, cancellationToken);
        if (refusal != null)
        {
            return refusal;
        }

        IssuedVerificationLink link = await IssueFreshLinkAsync(writer, account, correlationId, cancellationToken);
        await writer.CommitAsync(cancellationToken);

        return new ResendVerificationEmailResult
        {
            Outcome = ResendVerificationEmailOutcome.Sent,
            Response = new ResendVerificationEmailResponse(account.Email, link.ExpiresAt),
            UserId = account.UserId,
        };
    }

    private async Task<ResendVerificationEmailResult?> RefuseAsync(
        IWriterConnection writer, AccountRow account, CancellationToken cancellationToken)
    {
        AccountStatus status = Enum.Parse<AccountStatus>(account.Status);
        if (status != AccountStatus.PendingVerification)
        {
            return new ResendVerificationEmailResult
            {
                Outcome = ResendVerificationEmailOutcome.EmailAlreadyVerified,
                AccountStatus = status,
                UserId = account.UserId,
            };
        }

        DateTime databaseNow = await writer.Connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            SelectNowSql, transaction: writer.Transaction, cancellationToken: cancellationToken));
        var now = new DateTimeOffset(DateTime.SpecifyKind(databaseNow, DateTimeKind.Utc));
        IEnumerable<DateTime> resends = await writer.Connection.QueryAsync<DateTime>(new CommandDefinition(
            SelectRecentResendsSql, new { account.UserId, Since = now - _limitWindow }, writer.Transaction,
            cancellationToken: cancellationToken));
        var acceptedResends = new List<DateTimeOffset>();
        foreach (DateTime issuedAt in resends)
        {
            acceptedResends.Add(new DateTimeOffset(DateTime.SpecifyKind(issuedAt, DateTimeKind.Utc)));
        }

        TimeSpan retryAfter = ResendLimits.GetRetryAfter(acceptedResends, now);
        if (retryAfter <= TimeSpan.Zero)
        {
            return null;
        }

        return new ResendVerificationEmailResult
        {
            Outcome = ResendVerificationEmailOutcome.RateLimited,
            RetryAfter = retryAfter,
            UserId = account.UserId,
        };
    }

    // Only the newest link works: every earlier unused one is voided before the new one is issued.
    private async Task<IssuedVerificationLink> IssueFreshLinkAsync(
        IWriterConnection writer, AccountRow account, string correlationId, CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            VoidUnusedLinksSql, new { account.UserId }, writer.Transaction,
            cancellationToken: cancellationToken));
        IssuedVerificationLink link = await _verificationLinks.IssueAsync(writer, account.UserId, cancellationToken);

        // A resend records a fresh UserRegistered: the system event catalog has no separate resend event.
        var data = new
        {
            userId = account.UserId,
            email = account.Email,
            name = account.FullName,
            role = account.Role,
            verificationLink = link.Link,
        };
        await IdentityEventRecorder.RecordAsync(writer, IdentityEventRecorder.UserRegistered, data, correlationId, cancellationToken);
        return link;
    }

    private sealed record AccountKey(Guid UserId, string Email);

    private sealed class AccountRow
    {
        public Guid UserId { get; init; }

        public string Email { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string Role { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Authentication")]
[Route("auth/verificationEmails")]
public sealed class ResendVerificationEmailController : ControllerBase
{
    private readonly ResendVerificationEmailHandler _handler;
    private readonly ResendVerificationEmailValidator _validator = new ResendVerificationEmailValidator();
    private readonly ILogger<ResendVerificationEmailController> _logger;

    public ResendVerificationEmailController(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory,
        VerificationLinks verificationLinks, ILogger<ResendVerificationEmailController> logger)
    {
        _handler = new ResendVerificationEmailHandler(readerFactory, writerFactory, verificationLinks);
        _logger = logger;
    }

    /// <summary>Send a new verification link.</summary>
    /// <response code="200">A new link was issued and the email is on its way.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="422">No link can be sent for this email (EMAIL_NOT_REGISTERED, EMAIL_ALREADY_VERIFIED).</response>
    /// <response code="429">Too many resend requests for this email (RATE_LIMITED).</response>
    [AllowAnonymous] // Public by design: the person cannot sign in until their email is verified.
    [HttpPost]
    [ProducesResponseType(typeof(ResendVerificationEmailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResendVerificationEmail(
        ResendVerificationEmailRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        ResendVerificationEmailResult result = await _handler.HandleAsync(request, correlationId, cancellationToken);
        if (result.Outcome != ResendVerificationEmailOutcome.Sent)
        {
            // Security review: refusals and rate-limit hits, by account, never with the email itself.
            _logger.LogWarning("Verification resend refused: {Outcome} for account {UserId}", result.Outcome, result.UserId);
        }

        switch (result.Outcome)
        {
            case ResendVerificationEmailOutcome.Sent:
                return Ok(result.Response);
            case ResendVerificationEmailOutcome.EmailNotRegistered:
                return ProblemResult.EmailNotRegistered(this, $"No account uses {request.Email}.");
            case ResendVerificationEmailOutcome.EmailAlreadyVerified:
                string accountStatus = JsonNamingPolicy.CamelCase.ConvertName(result.AccountStatus!.Value.ToString());
                return ProblemResult.EmailAlreadyVerified(this, "This email is already verified; sign in instead.", accountStatus);
            case ResendVerificationEmailOutcome.RateLimited:
                int retryAfterSeconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds);
                return ProblemResult.RateLimited(this, "Too many verification emails were requested.", retryAfterSeconds);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
