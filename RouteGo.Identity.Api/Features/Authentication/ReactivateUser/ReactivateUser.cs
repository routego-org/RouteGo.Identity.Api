using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.ReactivateUser;

/// <summary>Why the admin is lifting the suspension.</summary>
/// <param name="Reason">10–500 characters; recorded with the suspension and in the audit trail.</param>
public sealed record ReactivateUserRequest(string? Reason);

/// <summary>The account's summary after the reactivation.</summary>
public sealed class ReactivateUserResponse
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

public enum ReactivateUserOutcome
{
    Reactivated,
    NotFound,
    NotSuspended,
}

/// <summary>The outcome, the reactivated account on success, and the status that refused it otherwise.</summary>
/// <param name="Outcome">How the reactivation ended.</param>
/// <param name="Response">The account's summary when reactivated.</param>
/// <param name="CurrentStatus">The account's status when it was not Suspended.</param>
public sealed record ReactivateUserResult(
    ReactivateUserOutcome Outcome, ReactivateUserResponse? Response = null, AccountStatus? CurrentStatus = null);

/// <summary>The reactivating admin and the request that carried the reactivation.</summary>
/// <param name="AdminUserId">The signed-in admin, from the access token.</param>
/// <param name="CorrelationId">The request's correlation ID, carried onto the audit record.</param>
public sealed record ReactivateUserCaller(Guid AdminUserId, string CorrelationId);

public sealed class ReactivateUserValidator : AbstractValidator<ReactivateUserRequest>
{
    public ReactivateUserValidator()
    {
        RuleFor(x => x.Reason).Must(SuspensionRules.IsAcceptableReason)
            .WithMessage("The reason must be 10 to 500 characters.");
    }
}

public sealed class ReactivateUserHandler
{
    private const string AuditAction = "ReactivateUser";
    private const string UserReactivatedAction = "UserReactivated";
    private const string AccountEntity = "Account";

    private const string SelectAccountForUpdateSql = """
        SELECT user_id, email, full_name, phone, role, status, created_at
        FROM authentication.users
        WHERE user_id = @UserId
        FOR UPDATE
        """;

    private const string ReactivateAccountSql = """
        UPDATE authentication.users SET status = @Status WHERE user_id = @UserId
        """;

    // Closes the current suspension; earlier ones keep their history untouched.
    private const string CloseSuspensionSql = """
        UPDATE authentication.account_suspensions
        SET reactivated_at = now(), reactivated_by = @ReactivatedBy, reactivate_reason = @Reason
        WHERE user_id = @UserId AND reactivated_at IS NULL
        """;

    private readonly IWriterConnectionFactory _writerFactory;

    public ReactivateUserHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>
    /// Moves a Suspended account back to Active. No session is restored: the person signs in again.
    /// </summary>
    public async Task<ReactivateUserResult> HandleAsync(
        Guid userId, ReactivateUserRequest request, ReactivateUserCaller caller, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        AccountRow? account = await writer.Connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            SelectAccountForUpdateSql, new { UserId = userId }, writer.Transaction, cancellationToken: cancellationToken));
        if (account is null)
        {
            return new ReactivateUserResult(ReactivateUserOutcome.NotFound);
        }

        AccountStatus status = Enum.Parse<AccountStatus>(account.Status);
        if (status != AccountStatus.Suspended)
        {
            return new ReactivateUserResult(ReactivateUserOutcome.NotSuspended, CurrentStatus: status);
        }

        await ReactivateAsync(writer, userId, request.Reason!, caller, cancellationToken);
        await RecordUserReactivatedAsync(writer, userId, request.Reason!, caller, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new ReactivateUserResult(ReactivateUserOutcome.Reactivated, ToResponse(account));
    }

    private static async Task ReactivateAsync(
        IWriterConnection writer, Guid userId, string reason, ReactivateUserCaller caller, CancellationToken cancellationToken)
    {
        var account = new { UserId = userId, Status = nameof(AccountStatus.Active) };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            ReactivateAccountSql, account, writer.Transaction, cancellationToken: cancellationToken));

        var suspension = new { UserId = userId, ReactivatedBy = caller.AdminUserId, Reason = reason };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            CloseSuspensionSql, suspension, writer.Transaction, cancellationToken: cancellationToken));
    }

    // Only the audit record: a reactivation notifies no service and sends no email.
    private static async Task RecordUserReactivatedAsync(
        IWriterConnection writer, Guid userId, string reason, ReactivateUserCaller caller, CancellationToken cancellationToken)
    {
        var audit = new AuditRecordedData
        {
            ActorId = caller.AdminUserId.ToString(),
            ActorRole = nameof(AccountRole.Admin),
            Action = UserReactivatedAction,
            EntityType = AccountEntity,
            EntityId = userId.ToString(),
            Details = new { reason },
        };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.AuditRecorded, audit, caller.CorrelationId, cancellationToken);
    }

    private static ReactivateUserResponse ToResponse(AccountRow account)
    {
        return new ReactivateUserResponse
        {
            UserId = account.UserId,
            Email = account.Email,
            FullName = account.FullName,
            Phone = account.Phone,
            Role = Enum.Parse<AccountRole>(account.Role),
            Status = AccountStatus.Active,
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
[Route("auth/admin/users/{userId}/reactivate")]
public sealed class ReactivateUserController : ControllerBase
{
    private readonly ReactivateUserHandler _handler;
    private readonly ReactivateUserValidator _validator = new ReactivateUserValidator();

    public ReactivateUserController(IWriterConnectionFactory writerFactory)
    {
        _handler = new ReactivateUserHandler(writerFactory);
    }

    /// <summary>Reactivate a suspended account (Admin only).</summary>
    /// <response code="200">The account is Active again.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an Admin (FORBIDDEN).</response>
    /// <response code="404">No account has this identifier (NOT_FOUND).</response>
    /// <response code="409">The account is not Suspended (USER_NOT_SUSPENDED, with currentStatus).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    [ProducesResponseType(typeof(ReactivateUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReactivateUser(Guid userId, ReactivateUserRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        var caller = new ReactivateUserCaller(
            Guid.Parse(User.FindFirst("sub")!.Value), CorrelationIdMiddleware.GetCorrelationId(HttpContext));
        ReactivateUserResult result = await _handler.HandleAsync(userId, request, caller, cancellationToken);
        switch (result.Outcome)
        {
            case ReactivateUserOutcome.Reactivated:
                return Ok(result.Response);
            case ReactivateUserOutcome.NotFound:
                return ProblemResult.NotFound(this, "No account has this identifier.");
            case ReactivateUserOutcome.NotSuspended:
                string currentStatus = JsonNamingPolicy.CamelCase.ConvertName(result.CurrentStatus!.Value.ToString());
                return ProblemResult.UserNotSuspended(this, $"The account is {currentStatus}; only a suspended account can be reactivated.", currentStatus);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
