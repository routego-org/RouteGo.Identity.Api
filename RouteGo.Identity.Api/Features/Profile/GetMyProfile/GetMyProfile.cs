using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Profile.GetMyProfile;

/// <summary>The signed-in account's profile.</summary>
public sealed class GetMyProfileResponse
{
    public required Guid UserId { get; init; }

    public required string Email { get; init; }

    public required string FullName { get; init; }

    /// <summary>+91 followed by 10 digits, or null.</summary>
    public required string? Phone { get; init; }

    public required AccountRole Role { get; init; }
}

public sealed class GetMyProfileHandler
{
    private const string SelectProfileSql = """
        SELECT user_id, email, full_name, phone, role
        FROM authentication.users
        WHERE user_id = @UserId
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public GetMyProfileHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Reads the profile of the account the access token names.</summary>
    /// <param name="callerUserId">The signed-in account, from the access token.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<GetMyProfileResponse> HandleAsync(Guid callerUserId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = _readerFactory.CreateReadConnection();
        ProfileRow row = await connection.QuerySingleAsync<ProfileRow>(new CommandDefinition(
            SelectProfileSql, new { UserId = callerUserId }, cancellationToken: cancellationToken));
        return ToResponse(row);
    }

    private static GetMyProfileResponse ToResponse(ProfileRow row)
    {
        return new GetMyProfileResponse
        {
            UserId = row.UserId,
            Email = row.Email,
            FullName = row.FullName,
            Phone = row.Phone,
            Role = Enum.Parse<AccountRole>(row.Role),
        };
    }

    private sealed class ProfileRow
    {
        public Guid UserId { get; init; }

        public string Email { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string? Phone { get; init; }

        public string Role { get; init; } = string.Empty;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Profile")]
[Route("me")]
public sealed class GetMyProfileController : ControllerBase
{
    private readonly GetMyProfileHandler _handler;

    public GetMyProfileController(IReaderConnectionFactory readerFactory)
    {
        _handler = new GetMyProfileHandler(readerFactory);
    }

    /// <summary>View my profile.</summary>
    /// <response code="200">The caller's profile.</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    [Authorize]
    [HttpGet]
    [ProducesResponseType(typeof(GetMyProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyProfile(CancellationToken cancellationToken)
    {
        var callerUserId = Guid.Parse(User.FindFirst("sub")!.Value);
        GetMyProfileResponse response = await _handler.HandleAsync(callerUserId, cancellationToken);
        return Ok(response);
    }
}
