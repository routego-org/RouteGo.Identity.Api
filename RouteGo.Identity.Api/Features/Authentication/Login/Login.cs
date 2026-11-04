using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Common.Sessions;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.Login;

/// <summary>Sign-in credentials.</summary>
/// <param name="Email">Compared case-insensitively.</param>
/// <param name="Password">The account's password.</param>
public sealed record LoginRequest(string? Email, string? Password);

public enum LoginOutcome
{
    SignedIn,
    InvalidCredentials,
    EmailNotVerified,
    PendingApproval,
    Rejected,
    Suspended,
    Locked,
}

/// <summary>The outcome, the session on success, and what the controller reports or logs.</summary>
public sealed class LoginResult
{
    public required LoginOutcome Outcome { get; init; }

    public TokenPair? Tokens { get; init; }

    /// <summary>When the lock ends, for a locked account.</summary>
    public DateTimeOffset? LockedUntil { get; init; }

    /// <summary>Whether this attempt is the one that locked the account.</summary>
    public bool IsNewLock { get; init; }

    /// <summary>The account, for security logging; null when no account uses the email.</summary>
    public Guid? UserId { get; init; }
}

public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(AccountRules.MaxEmailLength);
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class LoginHandler
{
    private const string AuditAction = "SignIn";

    // A hash verified when no account exists, so an unknown email costs the same time as a wrong
    // password and response timing does not reveal which emails have accounts.
    private static readonly string _absentAccountHash = PasswordHashing.Hash("no account uses this email");

    private const string SelectAccountSql = """
        SELECT user_id, email FROM authentication.users WHERE lower(email) = lower(@Email)
        """;

    // The row lock serializes concurrent attempts on one account, so failures are counted exactly.
    private const string SelectForUpdateSql = """
        SELECT user_id, email, password_hash, role, status, failed_sign_in_count, first_failed_sign_in_at,
               locked_until, now() AS now
        FROM authentication.users
        WHERE user_id = @UserId
        FOR UPDATE
        """;

    private const string UpdateLockoutSql = """
        UPDATE authentication.users
        SET failed_sign_in_count = @FailedCount, first_failed_sign_in_at = @FirstFailedAt, locked_until = @LockedUntil
        WHERE user_id = @UserId
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IActorWriterConnectionFactory _writerFactory;
    private readonly SessionIssuer _sessionIssuer;

    public LoginHandler(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory, SessionIssuer sessionIssuer)
    {
        _readerFactory = readerFactory;
        _writerFactory = writerFactory;
        _sessionIssuer = sessionIssuer;
    }

    public async Task<LoginResult> HandleAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        // Only to name the actor (nobody is signed in yet); the decision re-reads the account under a lock.
        await using DbConnection reader = _readerFactory.CreateReadConnection();
        AccountKey? key = await reader.QuerySingleOrDefaultAsync<AccountKey>(new CommandDefinition(
            SelectAccountSql, new { request.Email }, cancellationToken: cancellationToken));
        if (key is null)
        {
            PasswordHashing.Verify(_absentAccountHash, request.Password!);
            return new LoginResult { Outcome = LoginOutcome.InvalidCredentials };
        }

        var actor = new ActorIdentity(key.UserId.ToString(), key.Email, key.Email);
        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(actor, AuditAction, cancellationToken);
        AccountRow account = await writer.Connection.QuerySingleAsync<AccountRow>(new CommandDefinition(
            SelectForUpdateSql, new { key.UserId }, writer.Transaction, cancellationToken: cancellationToken));

        LoginResult result = await DecideAsync(writer, account, request.Password!, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return result;
    }

    // The order is the spec's: lock, then password, then status, then success. A status that
    // reveals something about the account is only ever given to someone who knows the password.
    private async Task<LoginResult> DecideAsync(
        IWriterConnection writer, AccountRow account, string password, CancellationToken cancellationToken)
    {
        DateTimeOffset now = AsUtc(account.Now);
        LockoutState lockout = account.ToLockout();
        if (Lockout.IsLocked(lockout, now))
        {
            return new LoginResult { Outcome = LoginOutcome.Locked, LockedUntil = lockout.LockedUntil, UserId = account.UserId };
        }

        if (!PasswordHashing.Verify(account.PasswordHash, password))
        {
            LockoutState failed = Lockout.RecordFailure(lockout, now);
            await SaveLockoutAsync(writer, account.UserId, failed, cancellationToken);
            bool isNewLock = failed.LockedUntil.HasValue;
            LoginOutcome outcome = isNewLock ? LoginOutcome.Locked : LoginOutcome.InvalidCredentials;
            return new LoginResult { Outcome = outcome, LockedUntil = failed.LockedUntil, IsNewLock = isNewLock, UserId = account.UserId };
        }

        LoginOutcome? refusal = RefuseByStatus(Enum.Parse<AccountStatus>(account.Status));
        if (refusal.HasValue)
        {
            return new LoginResult { Outcome = refusal.Value, UserId = account.UserId };
        }

        await SaveLockoutAsync(writer, account.UserId, Lockout.Cleared, cancellationToken);
        var subject = new SessionSubject(account.UserId, account.Email, Enum.Parse<AccountRole>(account.Role));
        IssuedSession session = await _sessionIssuer.IssueAsync(writer, subject, Guid.NewGuid(), cancellationToken);
        return new LoginResult { Outcome = LoginOutcome.SignedIn, Tokens = session.Tokens, UserId = account.UserId };
    }

    private static LoginOutcome? RefuseByStatus(AccountStatus status)
    {
        switch (status)
        {
            case AccountStatus.Active:
                return null;
            case AccountStatus.PendingVerification:
                return LoginOutcome.EmailNotVerified;
            case AccountStatus.PendingApproval:
                return LoginOutcome.PendingApproval;
            case AccountStatus.Rejected:
                return LoginOutcome.Rejected;
            case AccountStatus.Suspended:
                return LoginOutcome.Suspended;
            default:
                throw new InvalidOperationException($"Unhandled account status '{status}'.");
        }
    }

    private static async Task SaveLockoutAsync(
        IWriterConnection writer, Guid userId, LockoutState lockout, CancellationToken cancellationToken)
    {
        var parameters = new { UserId = userId, lockout.FailedCount, lockout.FirstFailedAt, lockout.LockedUntil };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            UpdateLockoutSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static DateTimeOffset AsUtc(DateTime utc)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed record AccountKey(Guid UserId, string Email);

    private sealed class AccountRow
    {
        public Guid UserId { get; init; }

        public string Email { get; init; } = string.Empty;

        public string PasswordHash { get; init; } = string.Empty;

        public string Role { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public int FailedSignInCount { get; init; }

        public DateTime? FirstFailedSignInAt { get; init; }

        public DateTime? LockedUntil { get; init; }

        public DateTime Now { get; init; }

        public LockoutState ToLockout()
        {
            DateTimeOffset? firstFailedAt = null;
            if (FirstFailedSignInAt.HasValue)
            {
                firstFailedAt = AsUtc(FirstFailedSignInAt.Value);
            }

            DateTimeOffset? lockedUntil = null;
            if (LockedUntil.HasValue)
            {
                lockedUntil = AsUtc(LockedUntil.Value);
            }

            return new LockoutState(FailedSignInCount, firstFailedAt, lockedUntil);
        }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Authentication")]
[Route("auth/sessions")]
public sealed class LoginController : ControllerBase
{
    private readonly LoginHandler _handler;
    private readonly LoginValidator _validator = new LoginValidator();
    private readonly ILogger<LoginController> _logger;

    public LoginController(
        IReaderConnectionFactory readerFactory, IActorWriterConnectionFactory writerFactory, SessionIssuer sessionIssuer,
        ILogger<LoginController> logger)
    {
        _handler = new LoginHandler(readerFactory, writerFactory, sessionIssuer);
        _logger = logger;
    }

    /// <summary>Sign in with email and password.</summary>
    /// <response code="200">Signed in.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">The email or password is incorrect (INVALID_CREDENTIALS).</response>
    /// <response code="403">The password is correct but this account may not sign in now (EMAIL_NOT_VERIFIED, ACCOUNT_PENDING_APPROVAL, ACCOUNT_REJECTED, ACCOUNT_SUSPENDED, ACCOUNT_LOCKED).</response>
    [AllowAnonymous] // Public by design: this is where a token is obtained.
    [HttpPost]
    [ProducesResponseType(typeof(TokenPair), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        LoginResult result = await _handler.HandleAsync(request, cancellationToken);
        LogSecurityEvent(result);
        switch (result.Outcome)
        {
            case LoginOutcome.SignedIn:
                return Ok(result.Tokens);
            case LoginOutcome.InvalidCredentials:
                return ProblemResult.InvalidCredentials(this, "Check the email and password and try again.");
            case LoginOutcome.EmailNotVerified:
                return ProblemResult.EmailNotVerified(this, "Open the link in the verification email, or ask for a new one.");
            case LoginOutcome.PendingApproval:
                return ProblemResult.AccountPendingApproval(this, "An admin is reviewing the application; you will be emailed.");
            case LoginOutcome.Rejected:
                return ProblemResult.AccountRejected(this, "The operator application was rejected.");
            case LoginOutcome.Suspended:
                return ProblemResult.AccountSuspended(this, "Contact support about this account.");
            case LoginOutcome.Locked:
                return ProblemResult.AccountLocked(this, "Too many failed sign-ins; try again later.", result.LockedUntil!.Value);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }

    // Failed sign-ins and lockouts are logged with the account so attacks are visible; never the password.
    private void LogSecurityEvent(LoginResult result)
    {
        if (result.IsNewLock)
        {
            _logger.LogWarning("Account {UserId} locked until {LockedUntil} after repeated failed sign-ins", result.UserId, result.LockedUntil);
            return;
        }

        if (result.Outcome == LoginOutcome.Locked)
        {
            // Attempts that keep arriving while the account is locked are the clearest sign of guessing.
            _logger.LogWarning("Sign-in refused for locked account {UserId}, locked until {LockedUntil}", result.UserId, result.LockedUntil);
            return;
        }

        if (result.Outcome == LoginOutcome.InvalidCredentials)
        {
            _logger.LogWarning("Failed sign-in for account {UserId}", result.UserId);
        }
    }
}
