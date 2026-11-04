using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.OperatorOnboarding.RejectOperator;

/// <summary>Why the admin is rejecting the application.</summary>
/// <param name="Reason">10–500 characters; emailed to the operator and kept with the application.</param>
public sealed record RejectOperatorRequest(string? Reason);

public enum RejectOperatorOutcome
{
    Rejected,
    NotFound,
    NotPending,
}

/// <summary>The outcome, the rejected application on success, and the status that refused it otherwise.</summary>
/// <param name="Outcome">How the rejection ended.</param>
/// <param name="Application">The rejected application.</param>
/// <param name="CurrentStatus">The latest application's status when it was not awaiting review.</param>
public sealed record RejectOperatorResult(
    RejectOperatorOutcome Outcome, OperatorApplicationResponse? Application = null, ApplicationStatus? CurrentStatus = null);

/// <summary>The rejecting admin and the request that carried the rejection.</summary>
/// <param name="AdminUserId">The signed-in admin, from the access token.</param>
/// <param name="CorrelationId">The request's correlation ID, carried onto both events.</param>
public sealed record RejectOperatorCaller(Guid AdminUserId, string CorrelationId);

public sealed class RejectOperatorValidator : AbstractValidator<RejectOperatorRequest>
{
    public RejectOperatorValidator()
    {
        RuleFor(x => x.Reason).Must(OperatorApplicationRules.IsAcceptableRejectReason)
            .WithMessage("The reason must be 10 to 500 characters.");
    }
}

public sealed class RejectOperatorHandler
{
    private const string AuditAction = "RejectOperator";
    private const string OperatorRejectedAction = "OperatorRejected";
    private const string OperatorEntity = "Operator";

    // The account row is the lock: approve, reject and re-apply for one operator take it first, so
    // two admins deciding at once are serialized and the second sees the first's decision.
    private const string SelectOperatorForUpdateSql = """
        SELECT email FROM authentication.users
        WHERE user_id = @OperatorId AND role = 'Operator'
        FOR UPDATE
        """;

    private const string SelectLatestApplicationSql = """
        SELECT application_id, status
        FROM operator_onboarding.operator_applications
        WHERE operator_id = @OperatorId
        ORDER BY submitted_at DESC
        LIMIT 1
        """;

    // Rejected no longer holds the GSTIN: the partial unique index covers pending and approved only.
    private const string RejectApplicationSql = """
        UPDATE operator_onboarding.operator_applications
        SET status = 'Rejected', reviewed_at = now(), reviewed_by = @ReviewedBy, reject_reason = @Reason
        WHERE application_id = @ApplicationId
        """;

    private const string RejectOperatorSql = """
        UPDATE authentication.users SET status = 'Rejected' WHERE user_id = @OperatorId
        """;

    private readonly IWriterConnectionFactory _writerFactory;

    public RejectOperatorHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>
    /// Rejects the operator's application awaiting review: the application, the account and both
    /// events commit together, or nothing changes. The rejection starts the re-application wait.
    /// </summary>
    public async Task<RejectOperatorResult> HandleAsync(
        Guid operatorId, RejectOperatorRequest request, RejectOperatorCaller caller, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        string? email = await writer.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            SelectOperatorForUpdateSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (email is null)
        {
            return new RejectOperatorResult(RejectOperatorOutcome.NotFound);
        }

        ApplicationRow application = await writer.Connection.QuerySingleAsync<ApplicationRow>(new CommandDefinition(
            SelectLatestApplicationSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        ApplicationStatus status = Enum.Parse<ApplicationStatus>(application.Status);
        if (status != ApplicationStatus.PendingApproval)
        {
            return new RejectOperatorResult(RejectOperatorOutcome.NotPending, CurrentStatus: status);
        }

        var decision = new Decision(operatorId, email, application.ApplicationId, request.Reason!);
        await RejectAsync(writer, decision, caller, cancellationToken);
        await RecordEventsAsync(writer, decision, caller, cancellationToken);
        IReadOnlyList<OperatorApplicationResponse> rejected = await OperatorApplicationReader.ReadAsync(
            writer.Connection, writer.Transaction, new[] { application.ApplicationId }, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new RejectOperatorResult(RejectOperatorOutcome.Rejected, rejected[0]);
    }

    private static async Task RejectAsync(
        IWriterConnection writer, Decision decision, RejectOperatorCaller caller, CancellationToken cancellationToken)
    {
        var rejection = new { decision.ApplicationId, ReviewedBy = caller.AdminUserId, decision.Reason };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            RejectApplicationSql, rejection, writer.Transaction, cancellationToken: cancellationToken));
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            RejectOperatorSql, new { decision.OperatorId }, writer.Transaction, cancellationToken: cancellationToken));
    }

    // OperatorRejected tells Notification to email the company the reason; the AuditRecorded copy
    // is the audit trail.
    private static async Task RecordEventsAsync(
        IWriterConnection writer, Decision decision, RejectOperatorCaller caller, CancellationToken cancellationToken)
    {
        var rejected = new { operatorId = decision.OperatorId, email = decision.Email, reason = decision.Reason };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.OperatorRejected, rejected, caller.CorrelationId, cancellationToken);

        var audit = new AuditRecordedData
        {
            ActorId = caller.AdminUserId.ToString(),
            ActorRole = nameof(AccountRole.Admin),
            Action = OperatorRejectedAction,
            EntityType = OperatorEntity,
            EntityId = decision.OperatorId.ToString(),
            Details = new { reason = decision.Reason },
        };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.AuditRecorded, audit, caller.CorrelationId, cancellationToken);
    }

    private sealed class Decision
    {
        public Decision(Guid operatorId, string email, Guid applicationId, string reason)
        {
            OperatorId = operatorId;
            Email = email;
            ApplicationId = applicationId;
            Reason = reason;
        }

        public Guid OperatorId { get; }

        public string Email { get; }

        public Guid ApplicationId { get; }

        public string Reason { get; }
    }

    private sealed class ApplicationRow
    {
        public Guid ApplicationId { get; init; }

        public string Status { get; init; } = string.Empty;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "OperatorOnboarding")]
[Route("operators/admin/applications/{operatorId}/reject")]
public sealed class RejectOperatorController : ControllerBase
{
    private readonly RejectOperatorHandler _handler;
    private readonly RejectOperatorValidator _validator = new RejectOperatorValidator();

    public RejectOperatorController(IWriterConnectionFactory writerFactory)
    {
        _handler = new RejectOperatorHandler(writerFactory);
    }

    /// <summary>Reject an operator's pending application with a reason (Admin only).</summary>
    /// <response code="200">The application is rejected.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an Admin (FORBIDDEN).</response>
    /// <response code="404">The identifier is not an operator account (NOT_FOUND).</response>
    /// <response code="409">The application is not awaiting review (APPLICATION_NOT_PENDING, with currentStatus).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    [ProducesResponseType(typeof(OperatorApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectOperator(Guid operatorId, RejectOperatorRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        var caller = new RejectOperatorCaller(
            Guid.Parse(User.FindFirst("sub")!.Value), CorrelationIdMiddleware.GetCorrelationId(HttpContext));
        RejectOperatorResult result = await _handler.HandleAsync(operatorId, request, caller, cancellationToken);
        switch (result.Outcome)
        {
            case RejectOperatorOutcome.Rejected:
                return Ok(result.Application);
            case RejectOperatorOutcome.NotFound:
                return ProblemResult.NotFound(this, "No operator account has this identifier.");
            case RejectOperatorOutcome.NotPending:
                string currentStatus = JsonNamingPolicy.CamelCase.ConvertName(result.CurrentStatus!.Value.ToString());
                return ProblemResult.ApplicationNotPending(this, $"The application is {currentStatus}.", currentStatus);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
