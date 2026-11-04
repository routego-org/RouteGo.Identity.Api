using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.SuspendUser;

/// <summary>Why the admin is suspending the account.</summary>
/// <param name="Reason">10–500 characters; recorded with the suspension and in the audit trail.</param>
public sealed record SuspendUserRequest(string? Reason);

/// <summary>The account's summary after the suspension.</summary>
public sealed class SuspendUserResponse
{
    public required Guid UserId { get; init; }

    public required string Email { get; init; }

    public required string FullName { get; init; }

    /// <summary>+91 followed by 10 digits, or null.</summary>
    public required string? Phone { get; init; }

    public required AccountRole Role { get; init; }

    public required AccountStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public enum SuspendUserOutcome
{
    Suspended,
    NotFound,
    AdminCannotBeSuspended,
    NotActive,
}

/// <summary>The outcome, the suspended account on success, and the status that refused it otherwise.</summary>
/// <param name="Outcome">How the suspension ended.</param>
/// <param name="Response">The account's summary when suspended.</param>
/// <param name="CurrentStatus">The account's status when it was not Active.</param>
public sealed record SuspendUserResult(
    SuspendUserOutcome Outcome, SuspendUserResponse? Response = null, AccountStatus? CurrentStatus = null);

/// <summary>The suspending admin and the request that carried the suspension.</summary>
/// <param name="AdminUserId">The signed-in admin, from the access token.</param>
/// <param name="CorrelationId">The request's correlation ID, carried onto the audit record.</param>
public sealed record SuspendUserCaller(Guid AdminUserId, string CorrelationId);

public sealed class SuspendUserValidator : AbstractValidator<SuspendUserRequest>
{
    public SuspendUserValidator()
    {
        RuleFor(x => x.Reason).Must(SuspensionRules.IsAcceptableReason)
            .WithMessage("The reason must be 10 to 500 characters.");
    }
}

public sealed class SuspendUserHandler
{
    private const string AuditAction = "SuspendUser";
    private const string UserSuspendedAction = "UserSuspended";
    private const string AccountEntity = "Account";

    private const string SelectAccountForUpdateSql = """
        SELECT user_id, email, full_name, phone, role, status, created_at
        FROM authentication.users
        WHERE user_id = @UserId
        FOR UPDATE
        """;

    private const string SuspendAccountSql = """
        UPDATE authentication.users SET status = @Status WHERE user_id = @UserId
        """;

    private const string InsertSuspensionSql = """
        INSERT INTO authentication.account_suspensions (user_id, suspended_at, suspended_by, suspend_reason)
        VALUES (@UserId, now(), @SuspendedBy, @Reason)
        """;

    // Every session ends with the suspension; access tokens already issued run out on their own.
    private const string RevokeAllRefreshTokensSql = """
        UPDATE authentication.refresh_tokens
        SET revoked_at = now(), revoke_reason = 'AccountSuspended'
        WHERE user_id = @UserId AND revoked_at IS NULL
        """;

    private readonly IWriterConnectionFactory _writerFactory;

    public SuspendUserHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>
    /// Suspends an Active customer or operator: the status change, the suspension record, the
    /// revocation of every refresh token and the audit event commit together or not at all.
    /// </summary>
    public async Task<SuspendUserResult> HandleAsync(
        Guid userId, SuspendUserRequest request, SuspendUserCaller caller, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        AccountRow? account = await writer.Connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            SelectAccountForUpdateSql, new { UserId = userId }, writer.Transaction, cancellationToken: cancellationToken));

        SuspendUserResult? refusal = Decide(account);
        if (refusal != null)
        {
            return refusal;
        }

        await SuspendAsync(writer, userId, request.Reason!, caller, cancellationToken);
        await RecordUserSuspendedAsync(writer, userId, request.Reason!, caller, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new SuspendUserResult(SuspendUserOutcome.Suspended, ToResponse(account!));
    }

    // Admins first: an Active admin is refused for being an admin, not for its status.
    private static SuspendUserResult? Decide(AccountRow? account)
    {
        if (account is null)
        {
            return new SuspendUserResult(SuspendUserOutcome.NotFound);
        }

        if (!SuspensionRules.CanBeSuspended(Enum.Parse<AccountRole>(account.Role)))
        {
            return new SuspendUserResult(SuspendUserOutcome.AdminCannotBeSuspended);
        }

        AccountStatus status = Enum.Parse<AccountStatus>(account.Status);
        if (status != AccountStatus.Active)
        {
            return new SuspendUserResult(SuspendUserOutcome.NotActive, CurrentStatus: status);
        }

        return null;
    }

    private static async Task SuspendAsync(
        IWriterConnection writer, Guid userId, string reason, SuspendUserCaller caller, CancellationToken cancellationToken)
    {
        var account = new { UserId = userId, Status = nameof(AccountStatus.Suspended) };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            SuspendAccountSql, account, writer.Transaction, cancellationToken: cancellationToken));

        var suspension = new { UserId = userId, SuspendedBy = caller.AdminUserId, Reason = reason };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertSuspensionSql, suspension, writer.Transaction, cancellationToken: cancellationToken));

        await writer.Connection.ExecuteAsync(new CommandDefinition(
            RevokeAllRefreshTokensSql, new { UserId = userId }, writer.Transaction, cancellationToken: cancellationToken));
    }

    // Only the audit record: a suspension notifies no service and sends no email.
    private static async Task RecordUserSuspendedAsync(
        IWriterConnection writer, Guid userId, string reason, SuspendUserCaller caller, CancellationToken cancellationToken)
    {
        var audit = new AuditRecordedData
        {
            ActorId = caller.AdminUserId.ToString(),
            ActorRole = nameof(AccountRole.Admin),
            Action = UserSuspendedAction,
            EntityType = AccountEntity,
            EntityId = userId.ToString(),
            Details = new { reason },
        };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.AuditRecorded, audit, caller.CorrelationId, cancellationToken);
    }

    private static SuspendUserResponse ToResponse(AccountRow account)
    {
        return new SuspendUserResponse
        {
            UserId = account.UserId,
            Email = account.Email,
            FullName = account.FullName,
            Phone = account.Phone,
            Role = Enum.Parse<AccountRole>(account.Role),
            Status = AccountStatus.Suspended,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(account.CreatedAt, DateTimeKind.Utc)),
        };
    }

    private sealed class AccountRow
    {
        public Guid UserId { get; init; }

        public string Email { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string? Phone { get; init; }

        public string Role { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public DateTime CreatedAt { get; init; }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Authentication")]
[Route("auth/admin/users/{userId}/suspend")]
public sealed class SuspendUserController : ControllerBase
{
    private readonly SuspendUserHandler _handler;
    private readonly SuspendUserValidator _validator = new SuspendUserValidator();

    public SuspendUserController(IWriterConnectionFactory writerFactory)
    {
        _handler = new SuspendUserHandler(writerFactory);
    }

    /// <summary>Suspend an active customer or operator account (Admin only).</summary>
    /// <response code="200">The account is now Suspended.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an Admin (FORBIDDEN).</response>
    /// <response code="404">No account has this identifier (NOT_FOUND).</response>
    /// <response code="409">The account is not Active (USER_NOT_ACTIVE, with currentStatus).</response>
    /// <response code="422">The account is an Admin account (ADMIN_CANNOT_BE_SUSPENDED).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    [ProducesResponseType(typeof(SuspendUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SuspendUser(Guid userId, SuspendUserRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        var caller = new SuspendUserCaller(
            Guid.Parse(User.FindFirst("sub")!.Value), CorrelationIdMiddleware.GetCorrelationId(HttpContext));
        SuspendUserResult result = await _handler.HandleAsync(userId, request, caller, cancellationToken);
        switch (result.Outcome)
        {
            case SuspendUserOutcome.Suspended:
                return Ok(result.Response);
            case SuspendUserOutcome.NotFound:
                return ProblemResult.NotFound(this, "No account has this identifier.");
            case SuspendUserOutcome.AdminCannotBeSuspended:
                return ProblemResult.AdminCannotBeSuspended(this, "Admin accounts cannot be suspended.");
            case SuspendUserOutcome.NotActive:
                string currentStatus = JsonNamingPolicy.CamelCase.ConvertName(result.CurrentStatus!.Value.ToString());
                return ProblemResult.UserNotActive(this, $"The account is {currentStatus}; only an active account can be suspended.", currentStatus);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
