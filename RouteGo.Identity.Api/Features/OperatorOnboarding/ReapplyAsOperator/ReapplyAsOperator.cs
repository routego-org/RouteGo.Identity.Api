using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.OperatorOnboarding.ReapplyAsOperator;

/// <summary>A rejected operator's credentials and its fresh company details.</summary>
public sealed class ReapplyAsOperatorRequest
{
    /// <summary>The operator's sign-in email.</summary>
    public string? Email { get; init; }

    /// <summary>The operator's password; it proves who is re-applying.</summary>
    public string? Password { get; init; }

    /// <summary>2–150 characters.</summary>
    public string? CompanyName { get; init; }

    /// <summary>The company's business contact address.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>The company's 15-character GSTIN, upper case.</summary>
    public string? Gstin { get; init; }
}

public enum ReapplyAsOperatorOutcome
{
    Reapplied,
    InvalidCredentials,
    Locked,
    NotRejected,
    TooSoon,
    GstinAlreadyRegistered,
}

/// <summary>The outcome, the new application on success, and what the controller reports or logs.</summary>
public sealed class ReapplyAsOperatorResult
{
    public required ReapplyAsOperatorOutcome Outcome { get; init; }

    public OperatorApplicationResponse? Application { get; init; }

    /// <summary>When the lock ends, for a locked account.</summary>
    public DateTimeOffset? LockedUntil { get; init; }

    /// <summary>Whether this attempt is the one that locked the account.</summary>
    public bool IsNewLock { get; init; }

    /// <summary>The account's status, when it is not a rejected operator.</summary>
    public AccountStatus? CurrentStatus { get; init; }

    /// <summary>The earliest re-application time, when it is too soon.</summary>
    public DateTimeOffset? ReapplyAllowedAt { get; init; }

    /// <summary>The account, for security logging; null when no account uses the email.</summary>
    public Guid? UserId { get; init; }
}

public sealed class ReapplyAsOperatorValidator : AbstractValidator<ReapplyAsOperatorRequest>
{
    public ReapplyAsOperatorValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(AccountRules.MaxEmailLength);
        RuleFor(x => x.Password).NotEmpty();
        RuleFor(x => x.CompanyName).Must(OperatorApplicationRules.IsAcceptableCompanyName)
            .WithMessage("The company name must be 2 to 150 characters.");
        RuleFor(x => x.ContactEmail).NotEmpty().EmailAddress().MaximumLength(OperatorApplicationRules.MaxContactEmailLength);
        RuleFor(x => x.Gstin).Must(OperatorApplicationRules.IsAcceptableGstin)
            .WithMessage("The GSTIN must be 15 characters in the standard format, upper case.");
    }
}

public sealed partial class ReapplyAsOperatorHandler
{
    private const string AuditAction = "ReapplyAsOperator";

    // A hash verified when no account exists, so an unknown email costs the same time as a wrong
    // password and response timing does not reveal which emails have accounts.
    private static readonly string _absentAccountHash = PasswordHashing.Hash("no account uses this email");

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IActorWriterConnectionFactory _writerFactory;

    public ReapplyAsOperatorHandler(IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory)
    {
        _readerFactory = readerFactory;
        _writerFactory = writerFactory;
    }

    /// <summary>
    /// Checks the credentials, then submits a new application straight to review. A wrong password
    /// counts toward the sign-in lockout and is kept even though the re-application is refused.
    /// </summary>
    public async Task<ReapplyAsOperatorResult> HandleAsync(ReapplyAsOperatorRequest request, CancellationToken cancellationToken)
    {
        // Only to name the actor (nobody is signed in); the decision re-reads the account under a lock.
        await using DbConnection reader = _readerFactory.CreateReadConnection();
        AccountKey? key = await reader.QuerySingleOrDefaultAsync<AccountKey>(new CommandDefinition(
            SelectAccountSql, new { request.Email }, cancellationToken: cancellationToken));
        if (key is null)
        {
            PasswordHashing.Verify(_absentAccountHash, request.Password!);
            return new ReapplyAsOperatorResult { Outcome = ReapplyAsOperatorOutcome.InvalidCredentials };
        }

        var actor = new ActorIdentity(key.UserId.ToString(), key.Email, key.Email);
        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(actor, AuditAction, cancellationToken);
        AccountRow account = await writer.Connection.QuerySingleAsync<AccountRow>(new CommandDefinition(
            SelectAccountForUpdateSql, new { key.UserId }, writer.Transaction, cancellationToken: cancellationToken));

        ReapplyAsOperatorResult result = await DecideAsync(writer, account, request, cancellationToken);
        if (IsCommitted(result))
        {
            await writer.CommitAsync(cancellationToken);
        }

        return result;
    }

    // A failed password attempt is a change worth keeping; every other refusal changes nothing.
    private static bool IsCommitted(ReapplyAsOperatorResult result)
    {
        return result.Outcome == ReapplyAsOperatorOutcome.Reapplied
            || result.Outcome == ReapplyAsOperatorOutcome.InvalidCredentials
            || result.IsNewLock;
    }

    // The order is the spec's: lock, then password, then status, then the wait, then the GSTIN.
    private static async Task<ReapplyAsOperatorResult> DecideAsync(
        IWriterConnection writer, AccountRow account, ReapplyAsOperatorRequest request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = AsUtc(account.Now);
        LockoutState lockout = account.ToLockout();
        if (Lockout.IsLocked(lockout, now))
        {
            return new ReapplyAsOperatorResult
            {
                Outcome = ReapplyAsOperatorOutcome.Locked,
                LockedUntil = lockout.LockedUntil,
                UserId = account.UserId,
            };
        }

        if (!PasswordHashing.Verify(account.PasswordHash, request.Password!))
        {
            return await RecordFailedAttemptAsync(writer, account, lockout, cancellationToken);
        }

        AccountStatus status = Enum.Parse<AccountStatus>(account.Status);
        if (account.Role != nameof(AccountRole.Operator) || status != AccountStatus.Rejected)
        {
            return new ReapplyAsOperatorResult { Outcome = ReapplyAsOperatorOutcome.NotRejected, CurrentStatus = status };
        }

        return await ReapplyAfterWaitAsync(writer, account, request, cancellationToken);
    }

    private static async Task<ReapplyAsOperatorResult> RecordFailedAttemptAsync(
        IWriterConnection writer, AccountRow account, LockoutState lockout, CancellationToken cancellationToken)
    {
        LockoutState failed = Lockout.RecordFailure(lockout, AsUtc(account.Now));
        await SaveLockoutAsync(writer, account.UserId, failed, cancellationToken);
        bool isNewLock = failed.LockedUntil.HasValue;
        ReapplyAsOperatorOutcome outcome = isNewLock ? ReapplyAsOperatorOutcome.Locked : ReapplyAsOperatorOutcome.InvalidCredentials;
        return new ReapplyAsOperatorResult
        {
            Outcome = outcome,
            LockedUntil = failed.LockedUntil,
            IsNewLock = isNewLock,
            UserId = account.UserId,
        };
    }

    private static async Task<ReapplyAsOperatorResult> ReapplyAfterWaitAsync(
        IWriterConnection writer, AccountRow account, ReapplyAsOperatorRequest request, CancellationToken cancellationToken)
    {
        DateTime rejectedAt = await writer.Connection.QuerySingleAsync<DateTime>(new CommandDefinition(
            SelectLatestRejectionSql, new { OperatorId = account.UserId }, writer.Transaction, cancellationToken: cancellationToken));
        DateTimeOffset reapplyAllowedAt = OperatorApplicationRules.ReapplyAllowedAt(AsUtc(rejectedAt));
        if (AsUtc(account.Now) < reapplyAllowedAt)
        {
            return new ReapplyAsOperatorResult { Outcome = ReapplyAsOperatorOutcome.TooSoon, ReapplyAllowedAt = reapplyAllowedAt };
        }

        Guid? applicationId = await TryInsertApplicationAsync(writer, account.UserId, request, cancellationToken);
        if (applicationId is null)
        {
            return new ReapplyAsOperatorResult { Outcome = ReapplyAsOperatorOutcome.GstinAlreadyRegistered };
        }

        await writer.Connection.ExecuteAsync(new CommandDefinition(
            SubmitOperatorForReviewSql, new { account.UserId }, writer.Transaction, cancellationToken: cancellationToken));
        IReadOnlyList<OperatorApplicationResponse> application = await OperatorApplicationReader.ReadAsync(
            writer.Connection, writer.Transaction, new[] { applicationId.Value }, cancellationToken);
        return new ReapplyAsOperatorResult { Outcome = ReapplyAsOperatorOutcome.Reapplied, Application = application[0] };
    }

    private static async Task<Guid?> TryInsertApplicationAsync(
        IWriterConnection writer, Guid operatorId, ReapplyAsOperatorRequest request, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            ApplicationId = Guid.NewGuid(),
            OperatorId = operatorId,
            request.CompanyName,
            request.ContactEmail,
            request.Gstin,
            Status = nameof(ApplicationStatus.PendingApproval),
        };
        return await writer.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            InsertApplicationSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task SaveLockoutAsync(
        IWriterConnection writer, Guid userId, LockoutState lockout, CancellationToken cancellationToken)
    {
        var parameters = new { UserId = userId, lockout.FailedCount, lockout.FirstFailedAt, lockout.LockedUntil };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            UpdateLockoutSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    // timestamptz reads back as a UTC DateTime.
    private static DateTimeOffset AsUtc(DateTime utc)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "OperatorOnboarding")]
[Route("operators/reapplications")]
public sealed class ReapplyAsOperatorController : ControllerBase
{
    private readonly ReapplyAsOperatorHandler _handler;
    private readonly ReapplyAsOperatorValidator _validator = new ReapplyAsOperatorValidator();
    private readonly ILogger<ReapplyAsOperatorController> _logger;

    public ReapplyAsOperatorController(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory,
        ILogger<ReapplyAsOperatorController> logger)
    {
        _handler = new ReapplyAsOperatorHandler(readerFactory, writerFactory);
        _logger = logger;
    }

    /// <summary>Re-apply after a rejection.</summary>
    /// <response code="201">The new application is awaiting admin review.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">The email or password is incorrect (INVALID_CREDENTIALS).</response>
    /// <response code="403">The account is locked after repeated failed attempts (ACCOUNT_LOCKED).</response>
    /// <response code="409">The account is not a rejected operator, or the GSTIN is taken (OPERATOR_NOT_REJECTED, GSTIN_ALREADY_REGISTERED).</response>
    /// <response code="422">The 7-day wait after the rejection has not passed (REAPPLICATION_TOO_SOON).</response>
    [AllowAnonymous] // Public by design: a rejected operator cannot sign in; the email and password are the credential.
    [HttpPost]
    [ProducesResponseType(typeof(OperatorApplicationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReapplyAsOperator(ReapplyAsOperatorRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        ReapplyAsOperatorResult result = await _handler.HandleAsync(request, cancellationToken);
        LogSecurityEvent(result);
        return ToActionResult(result, request);
    }

    private IActionResult ToActionResult(ReapplyAsOperatorResult result, ReapplyAsOperatorRequest request)
    {
        switch (result.Outcome)
        {
            case ReapplyAsOperatorOutcome.Reapplied:
                return StatusCode(StatusCodes.Status201Created, result.Application);
            case ReapplyAsOperatorOutcome.InvalidCredentials:
                return ProblemResult.InvalidCredentials(this, "Check the email and password and try again.");
            case ReapplyAsOperatorOutcome.Locked:
                return ProblemResult.AccountLocked(this, "Too many failed attempts; try again later.", result.LockedUntil!.Value);
            case ReapplyAsOperatorOutcome.NotRejected:
                string currentStatus = JsonNamingPolicy.CamelCase.ConvertName(result.CurrentStatus!.Value.ToString());
                return ProblemResult.OperatorNotRejected(this, $"The account is {currentStatus}.", currentStatus);
            case ReapplyAsOperatorOutcome.TooSoon:
                return ProblemResult.ReapplicationTooSoon(this, "The 7-day wait after the rejection has not passed.", result.ReapplyAllowedAt!.Value);
            case ReapplyAsOperatorOutcome.GstinAlreadyRegistered:
                return ProblemResult.GstinAlreadyRegistered(this, $"GSTIN {request.Gstin} is already registered; contact support.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }

    // Failed attempts and lockouts are logged with the account so attacks are visible; never the password.
    private void LogSecurityEvent(ReapplyAsOperatorResult result)
    {
        if (result.IsNewLock)
        {
            _logger.LogWarning("Account {UserId} locked until {LockedUntil} after repeated failed re-applications", result.UserId, result.LockedUntil);
            return;
        }

        if (result.Outcome == ReapplyAsOperatorOutcome.Locked)
        {
            _logger.LogWarning("Re-application refused for locked account {UserId}, locked until {LockedUntil}", result.UserId, result.LockedUntil);
            return;
        }

        if (result.Outcome == ReapplyAsOperatorOutcome.InvalidCredentials)
        {
            _logger.LogWarning("Failed re-application credentials for account {UserId}", result.UserId);
        }
    }
}
