using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.SearchUsers;

/// <summary>The search filters and the page wanted; every filter is optional and they combine with AND.</summary>
public sealed class SearchUsersRequest
{
    /// <summary>Case-insensitive partial match on the email address.</summary>
    public string? Email { get; init; }

    /// <summary>customer, operator or admin.</summary>
    public string? Role { get; init; }

    /// <summary>pendingVerification, pendingApproval, active, rejected or suspended.</summary>
    public string? Status { get; init; }

    /// <summary>1-based page number; 1 when absent.</summary>
    public int? Page { get; init; }

    /// <summary>Items per page, 1–200; 20 when absent.</summary>
    public int? PageSize { get; init; }

    /// <summary>The item field to sort by; newest account first when absent.</summary>
    public string? Sort { get; init; }

    /// <summary>Sort descending when true.</summary>
    public bool? Desc { get; init; }
}

/// <summary>One account as an admin sees it: never its password, tokens or lockout counters.</summary>
public sealed class UserSummary
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

/// <summary>One page of matching accounts.</summary>
public sealed class SearchUsersResponse
{
    public required IReadOnlyList<UserSummary> Items { get; init; }

    /// <summary>How many accounts match across all pages.</summary>
    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

public sealed class SearchUsersValidator : AbstractValidator<SearchUsersRequest>
{
    public SearchUsersValidator()
    {
        RuleFor(x => x.Email).MaximumLength(AccountRules.MaxEmailLength);
        RuleFor(x => x.Role).Must(IsAccountRole).When(x => x.Role != null)
            .WithMessage("The role must be customer, operator or admin.");
        RuleFor(x => x.Status).Must(IsAccountStatus).When(x => x.Status != null)
            .WithMessage("The status must be pendingVerification, pendingApproval, active, rejected or suspended.");
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(SearchUsersHandler.MinPageSize, SearchUsersHandler.MaxPageSize);
        RuleFor(x => x.Sort).Must(SearchUsersHandler.IsSortableField).When(x => x.Sort != null)
            .WithMessage("The sort must be a field of the account: userId, email, fullName, phone, role, status or createdAt.");
    }

    private static bool IsAccountRole(string? role)
    {
        return WireJson.TryReadEnum<AccountRole>(role, out _);
    }

    private static bool IsAccountStatus(string? status)
    {
        return WireJson.TryReadEnum<AccountStatus>(status, out _);
    }
}

public sealed class SearchUsersHandler
{
    /// <summary>The smallest page size a caller may ask for.</summary>
    public const int MinPageSize = 1;

    /// <summary>The largest page size a caller may ask for.</summary>
    public const int MaxPageSize = 200;

    private const int DefaultPageSize = 20;
    private const string NewestFirst = "created_at DESC";

    // Sort fields map from the item schema's names to columns through this list only, so nothing
    // a caller sends ever reaches the SQL text.
    private static readonly Dictionary<string, string> _sortColumns = new Dictionary<string, string>
    {
        ["userId"] = "user_id",
        ["email"] = "email",
        ["fullName"] = "full_name",
        ["phone"] = "phone",
        ["role"] = "role",
        ["status"] = "status",
        ["createdAt"] = "created_at",
    };

    private const string FilterSql = """
        WHERE (@Email IS NULL OR strpos(lower(email), lower(@Email)) > 0)
          AND (@Role IS NULL OR role = @Role)
          AND (@Status IS NULL OR status = @Status)
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public SearchUsersHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Whether a sort field names a field of the account summary.</summary>
    public static bool IsSortableField(string? field)
    {
        return field != null && _sortColumns.ContainsKey(field);
    }

    /// <summary>Lists one page of accounts matching the filters, with the total match count.</summary>
    public async Task<SearchUsersResponse> HandleAsync(SearchUsersRequest request, CancellationToken cancellationToken)
    {
        int page = request.Page ?? 1;
        int pageSize = request.PageSize ?? DefaultPageSize;
        var parameters = new
        {
            request.Email,
            Role = ToStoredRole(request.Role),
            Status = ToStoredStatus(request.Status),
            Offset = (page - 1) * pageSize,
            Limit = pageSize,
        };

        string sql = BuildSql(request.Sort, request.Desc == true);
        await using DbConnection connection = _readerFactory.CreateReadConnection();
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken));
        int totalCount = await results.ReadSingleAsync<int>();
        IEnumerable<UserRow> rows = await results.ReadAsync<UserRow>();

        var items = new List<UserSummary>();
        foreach (UserRow row in rows)
        {
            items.Add(ToSummary(row));
        }

        return new SearchUsersResponse { Items = items, TotalCount = totalCount, Page = page, PageSize = pageSize };
    }

    // The user id breaks ties, so paging is stable when many accounts share a sort value.
    private static string BuildSql(string? sort, bool isDescending)
    {
        string orderBy = NewestFirst;
        if (sort != null)
        {
            string direction = isDescending ? "DESC" : "ASC";
            orderBy = $"{_sortColumns[sort]} {direction}";
        }

        return $"""
            SELECT count(*)::int FROM authentication.users
            {FilterSql};
            SELECT user_id, email, full_name, phone, role, status, created_at
            FROM authentication.users
            {FilterSql}
            ORDER BY {orderBy}, user_id
            OFFSET @Offset LIMIT @Limit;
            """;
    }

    private static string? ToStoredRole(string? role)
    {
        if (!WireJson.TryReadEnum(role, out AccountRole value))
        {
            return null;
        }

        return value.ToString();
    }

    private static string? ToStoredStatus(string? status)
    {
        if (!WireJson.TryReadEnum(status, out AccountStatus value))
        {
            return null;
        }

        return value.ToString();
    }

    private static UserSummary ToSummary(UserRow row)
    {
        return new UserSummary
        {
            UserId = row.UserId,
            Email = row.Email,
            FullName = row.FullName,
            Phone = row.Phone,
            Role = Enum.Parse<AccountRole>(row.Role),
            Status = Enum.Parse<AccountStatus>(row.Status),
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
        };
    }

    private sealed class UserRow
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
[Route("auth/admin/users")]
public sealed class SearchUsersController : ControllerBase
{
    private readonly SearchUsersHandler _handler;
    private readonly SearchUsersValidator _validator = new SearchUsersValidator();

    public SearchUsersController(IReaderConnectionFactory readerFactory)
    {
        _handler = new SearchUsersHandler(readerFactory);
    }

    /// <summary>Search accounts (Admin only).</summary>
    /// <response code="200">The requested page of accounts; no match is an empty page.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an Admin (FORBIDDEN).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet]
    [ProducesResponseType(typeof(SearchUsersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SearchUsers([FromQuery] SearchUsersRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        SearchUsersResponse response = await _handler.HandleAsync(request, cancellationToken);
        return Ok(response);
    }
}
