using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.OperatorOnboarding.ApproveOperator;

public enum ApproveOperatorOutcome
{
    Approved,
    NotFound,
    NotPending,
}

/// <summary>The outcome, the approved application on success, and the status that refused it otherwise.</summary>
/// <param name="Outcome">How the approval ended.</param>
/// <param name="Application">The approved application.</param>
/// <param name="CurrentStatus">The latest application's status when it was not awaiting review.</param>
public sealed record ApproveOperatorResult(
    ApproveOperatorOutcome Outcome, OperatorApplicationResponse? Application = null, ApplicationStatus? CurrentStatus = null);

/// <summary>The approving admin and the request that carried the approval.</summary>
/// <param name="AdminUserId">The signed-in admin, from the access token.</param>
/// <param name="CorrelationId">The request's correlation ID, carried onto both events.</param>
public sealed record ApproveOperatorCaller(Guid AdminUserId, string CorrelationId);

public sealed class ApproveOperatorHandler
{
    private const string AuditAction = "ApproveOperator";
    private const string OperatorApprovedAction = "OperatorApproved";
    private const string OperatorEntity = "Operator";

    // The account row is the lock: approve, reject and re-apply for one operator take it first, so
    // two admins deciding at once are serialized and the second sees the first's decision.
    private const string SelectOperatorForUpdateSql = """
        SELECT email FROM authentication.users
        WHERE user_id = @OperatorId AND role = 'Operator'
        FOR UPDATE
        """;

    private const string SelectLatestApplicationSql = """
        SELECT application_id, company_name, gstin, status
        FROM operator_onboarding.operator_applications
        WHERE operator_id = @OperatorId
        ORDER BY submitted_at DESC
        LIMIT 1
        """;

    private const string ApproveApplicationSql = """
        UPDATE operator_onboarding.operator_applications
        SET status = 'Approved', reviewed_at = now(), reviewed_by = @ReviewedBy
        WHERE application_id = @ApplicationId
        """;

    private const string ActivateOperatorSql = """
        UPDATE authentication.users SET status = 'Active' WHERE user_id = @OperatorId
        """;

    private readonly IWriterConnectionFactory _writerFactory;

    public ApproveOperatorHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>
    /// Approves the operator's application awaiting review: the application, the account and both
    /// events commit together, or nothing changes.
    /// </summary>
    public async Task<ApproveOperatorResult> HandleAsync(
        Guid operatorId, ApproveOperatorCaller caller, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        string? email = await writer.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            SelectOperatorForUpdateSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (email is null)
        {
            return new ApproveOperatorResult(ApproveOperatorOutcome.NotFound);
        }

        ApplicationRow application = await writer.Connection.QuerySingleAsync<ApplicationRow>(new CommandDefinition(
            SelectLatestApplicationSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        ApplicationStatus status = Enum.Parse<ApplicationStatus>(application.Status);
        if (status != ApplicationStatus.PendingApproval)
        {
            return new ApproveOperatorResult(ApproveOperatorOutcome.NotPending, CurrentStatus: status);
        }

        var decision = new Decision(operatorId, email, application);
        await ApproveAsync(writer, decision, caller, cancellationToken);
        await RecordEventsAsync(writer, decision, caller, cancellationToken);
        IReadOnlyList<OperatorApplicationResponse> approved = await OperatorApplicationReader.ReadAsync(
            writer.Connection, writer.Transaction, new[] { application.ApplicationId }, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new ApproveOperatorResult(ApproveOperatorOutcome.Approved, approved[0]);
    }

    private static async Task ApproveAsync(
        IWriterConnection writer, Decision decision, ApproveOperatorCaller caller, CancellationToken cancellationToken)
    {
        var approval = new { decision.Application.ApplicationId, ReviewedBy = caller.AdminUserId };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            ApproveApplicationSql, approval, writer.Transaction, cancellationToken: cancellationToken));
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            ActivateOperatorSql, new { decision.OperatorId }, writer.Transaction, cancellationToken: cancellationToken));
    }

    // OperatorApproved tells Fleet to create the operator profile and Notification to email the
    // company; the AuditRecorded copy is the audit trail.
    private static async Task RecordEventsAsync(
        IWriterConnection writer, Decision decision, ApproveOperatorCaller caller, CancellationToken cancellationToken)
    {
        ApplicationRow application = decision.Application;
        var approved = new { operatorId = decision.OperatorId, companyName = application.CompanyName, email = decision.Email };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.OperatorApproved, approved, caller.CorrelationId, cancellationToken);

        var audit = new AuditRecordedData
        {
            ActorId = caller.AdminUserId.ToString(),
            ActorRole = nameof(AccountRole.Admin),
            Action = OperatorApprovedAction,
            EntityType = OperatorEntity,
            EntityId = decision.OperatorId.ToString(),
            Details = new { companyName = application.CompanyName, gstin = application.Gstin },
        };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.AuditRecorded, audit, caller.CorrelationId, cancellationToken);
    }

    /// <summary>The operator, its sign-in email, and the application being approved.</summary>
    /// <param name="OperatorId">The operator account.</param>
    /// <param name="Email">Its sign-in email, carried on OperatorApproved.</param>
    /// <param name="Application">The application awaiting review.</param>
    private sealed record Decision(Guid OperatorId, string Email, ApplicationRow Application);

    private sealed class ApplicationRow
    {
        public Guid ApplicationId { get; init; }

        public string CompanyName { get; init; } = string.Empty;

        public string Gstin { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "OperatorOnboarding")]
[Route("operators/admin/applications/{operatorId}/approve")]
public sealed class ApproveOperatorController : ControllerBase
{
    private readonly ApproveOperatorHandler _handler;

    public ApproveOperatorController(IWriterConnectionFactory writerFactory)
    {
        _handler = new ApproveOperatorHandler(writerFactory);
    }

    /// <summary>Approve an operator's pending application (Admin only).</summary>
    /// <response code="200">The application is approved.</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an Admin (FORBIDDEN).</response>
    /// <response code="404">The identifier is not an operator account (NOT_FOUND).</response>
    /// <response code="409">The application is not awaiting review (APPLICATION_NOT_PENDING, with currentStatus).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    [ProducesResponseType(typeof(OperatorApplicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApproveOperator(Guid operatorId, CancellationToken cancellationToken)
    {
        var caller = new ApproveOperatorCaller(
            Guid.Parse(User.FindFirst("sub")!.Value), CorrelationIdMiddleware.GetCorrelationId(HttpContext));
        ApproveOperatorResult result = await _handler.HandleAsync(operatorId, caller, cancellationToken);
        switch (result.Outcome)
        {
            case ApproveOperatorOutcome.Approved:
                return Ok(result.Application);
            case ApproveOperatorOutcome.NotFound:
                return ProblemResult.NotFound(this, "No operator account has this identifier.");
            case ApproveOperatorOutcome.NotPending:
                string currentStatus = JsonNamingPolicy.CamelCase.ConvertName(result.CurrentStatus!.Value.ToString());
                return ProblemResult.ApplicationNotPending(this, $"The application is {currentStatus}.", currentStatus);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
