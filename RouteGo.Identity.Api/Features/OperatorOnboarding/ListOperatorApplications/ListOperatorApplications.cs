using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.OperatorOnboarding.ListOperatorApplications;

/// <summary>Which applications to list, and the page wanted.</summary>
public sealed class ListOperatorApplicationsRequest
{
    /// <summary>pendingVerification, pendingApproval, approved or rejected; pendingApproval when absent.</summary>
    public string? Status { get; init; }

    /// <summary>1-based page number; 1 when absent.</summary>
    public int? Page { get; init; }

    /// <summary>Items per page, 1–200; 20 when absent.</summary>
    public int? PageSize { get; init; }
}

/// <summary>One page of applications, oldest submission first.</summary>
public sealed class ListOperatorApplicationsResponse
{
    public required IReadOnlyList<OperatorApplicationResponse> Items { get; init; }

    /// <summary>How many applications match across all pages.</summary>
    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

public sealed class ListOperatorApplicationsValidator : AbstractValidator<ListOperatorApplicationsRequest>
{
    public ListOperatorApplicationsValidator()
    {
        RuleFor(x => x.Status).Must(IsApplicationStatus).When(x => x.Status != null)
            .WithMessage("The status must be pendingVerification, pendingApproval, approved or rejected.");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(
            ListOperatorApplicationsHandler.MinPageSize, ListOperatorApplicationsHandler.MaxPageSize);
    }

    private static bool IsApplicationStatus(string? status)
    {
        return WireJson.TryReadEnum<ApplicationStatus>(status, out _);
    }
}

public sealed class ListOperatorApplicationsHandler
{
    /// <summary>The smallest page size a caller may ask for.</summary>
    public const int MinPageSize = 1;

    /// <summary>The largest page size a caller may ask for.</summary>
    public const int MaxPageSize = 200;

    private const int DefaultPageSize = 20;

    // Each operator's latest application only: an operator that re-applied appears once, as its
    // newest submission, with the earlier rejections listed under it.
    private const string SelectPageSql = """
        WITH latest AS (
            SELECT DISTINCT ON (operator_id) application_id, status, submitted_at
            FROM operator_onboarding.operator_applications
            ORDER BY operator_id, submitted_at DESC
        )
        SELECT count(*)::int FROM latest WHERE status = @Status;

        WITH latest AS (
            SELECT DISTINCT ON (operator_id) application_id, status, submitted_at
            FROM operator_onboarding.operator_applications
            ORDER BY operator_id, submitted_at DESC
        )
        SELECT application_id FROM latest
        WHERE status = @Status
        ORDER BY submitted_at, application_id
        OFFSET @Offset LIMIT @Limit;
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public ListOperatorApplicationsHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Lists one page of operators' latest applications in the requested status.</summary>
    public async Task<ListOperatorApplicationsResponse> HandleAsync(
        ListOperatorApplicationsRequest request, CancellationToken cancellationToken)
    {
        ApplicationStatus status = ToStatus(request.Status);
        int page = request.Page ?? 1;
        int pageSize = request.PageSize ?? DefaultPageSize;
        var parameters = new { Status = status.ToString(), Offset = (page - 1) * pageSize, Limit = pageSize };

        await using DbConnection connection = _readerFactory.CreateReadConnection();
        await connection.OpenAsync(cancellationToken);
        int totalCount;
        List<Guid> applicationIds;
        await using (SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectPageSql, parameters, cancellationToken: cancellationToken)))
        {
            totalCount = await results.ReadSingleAsync<int>();
            IEnumerable<Guid> ids = await results.ReadAsync<Guid>();
            applicationIds = ids.ToList();
        }

        IReadOnlyList<OperatorApplicationResponse> items = await OperatorApplicationReader.ReadAsync(
            connection, null, applicationIds, cancellationToken);
        return new ListOperatorApplicationsResponse { Items = items, TotalCount = totalCount, Page = page, PageSize = pageSize };
    }

    // The review queue is the default view: applications awaiting an admin.
    private static ApplicationStatus ToStatus(string? status)
    {
        if (!WireJson.TryReadEnum(status, out ApplicationStatus value))
        {
            return ApplicationStatus.PendingApproval;
        }

        return value;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "OperatorOnboarding")]
[Route("operators/admin/applications")]
public sealed class ListOperatorApplicationsController : ControllerBase
{
    private readonly ListOperatorApplicationsHandler _handler;
    private readonly ListOperatorApplicationsValidator _validator = new ListOperatorApplicationsValidator();

    public ListOperatorApplicationsController(IReaderConnectionFactory readerFactory)
    {
        _handler = new ListOperatorApplicationsHandler(readerFactory);
    }

    /// <summary>List operator applications (Admin only).</summary>
    /// <response code="200">The requested page; an empty match is an empty page.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an Admin (FORBIDDEN).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet]
    [ProducesResponseType(typeof(ListOperatorApplicationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListOperatorApplications(
        [FromQuery] ListOperatorApplicationsRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        ListOperatorApplicationsResponse response = await _handler.HandleAsync(request, cancellationToken);
        return Ok(response);
    }
}
