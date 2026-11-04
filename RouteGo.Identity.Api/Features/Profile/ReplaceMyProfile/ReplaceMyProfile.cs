using System.Text.Json.Serialization;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Profile.ReplaceMyProfile;

/// <summary>The editable fields of my profile, replaced as a whole.</summary>
public sealed class ReplaceMyProfileRequest
{
    private readonly string? _phone;

    /// <summary>2–100 characters.</summary>
    public string? FullName { get; init; }

    /// <summary>
    /// Required, but may be null to clear the phone. Otherwise an Indian mobile number: 10 digits
    /// starting 6–9, optionally prefixed +91; kept as +91 followed by the 10 digits.
    /// </summary>
    public string? Phone
    {
        get
        {
            return _phone;
        }

        init
        {
            _phone = value;
            IsPhoneSent = true;
        }
    }

    /// <summary>
    /// Whether the request carried a phone field at all. An explicit null clears the phone; a missing
    /// field is a malformed replacement, and the two read the same once deserialized.
    /// </summary>
    [JsonIgnore]
    public bool IsPhoneSent { get; private init; }
}

/// <summary>The profile after the replacement.</summary>
public sealed class ReplaceMyProfileResponse
{
    public required Guid UserId { get; init; }

    public required string Email { get; init; }

    public required string FullName { get; init; }

    /// <summary>+91 followed by 10 digits, or null.</summary>
    public required string? Phone { get; init; }

    public required AccountRole Role { get; init; }
}

public sealed class ReplaceMyProfileValidator : AbstractValidator<ReplaceMyProfileRequest>
{
    public ReplaceMyProfileValidator()
    {
        RuleFor(x => x.FullName).Must(AccountRules.IsAcceptableFullName)
            .WithMessage("The full name must be 2 to 100 characters.");
        RuleFor(x => x.Phone).Must(IsSent)
            .WithMessage("The phone must be sent; send null to clear it.");
        RuleFor(x => x.Phone).Must(AccountRules.IsAcceptablePhone).When(x => x.Phone != null)
            .WithMessage("The phone must be an Indian mobile number: 10 digits starting 6 to 9, optionally prefixed +91.");
    }

    private static bool IsSent(ReplaceMyProfileRequest request, string? phone)
    {
        return request.IsPhoneSent;
    }
}

public sealed class ReplaceMyProfileHandler
{
    private const string AuditAction = "ReplaceMyProfile";

    // Only the two editable columns are named: email, role and status cannot change here whatever
    // the request carried.
    private const string ReplaceProfileSql = """
        UPDATE authentication.users
        SET full_name = @FullName, phone = @Phone
        WHERE user_id = @UserId
        RETURNING user_id, email, full_name, phone, role
        """;

    private readonly IWriterConnectionFactory _writerFactory;

    public ReplaceMyProfileHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>Replaces the full name and phone of the account the access token names.</summary>
    /// <param name="callerUserId">The signed-in account, from the access token.</param>
    /// <param name="request">The new full name and phone.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<ReplaceMyProfileResponse> HandleAsync(
        Guid callerUserId, ReplaceMyProfileRequest request, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            UserId = callerUserId,
            request.FullName,
            Phone = AccountRules.NormalizePhone(request.Phone),
        };

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        ProfileRow row = await writer.Connection.QuerySingleAsync<ProfileRow>(new CommandDefinition(
            ReplaceProfileSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        await writer.CommitAsync(cancellationToken);
        return ToResponse(row);
    }

    private static ReplaceMyProfileResponse ToResponse(ProfileRow row)
    {
        return new ReplaceMyProfileResponse
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
public sealed class ReplaceMyProfileController : ControllerBase
{
    private readonly ReplaceMyProfileHandler _handler;
    private readonly ReplaceMyProfileValidator _validator = new ReplaceMyProfileValidator();

    public ReplaceMyProfileController(IWriterConnectionFactory writerFactory)
    {
        _handler = new ReplaceMyProfileHandler(writerFactory);
    }

    /// <summary>Replace my full name and phone.</summary>
    /// <response code="200">The updated profile.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    [Authorize]
    [HttpPut]
    [ProducesResponseType(typeof(ReplaceMyProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReplaceMyProfile(ReplaceMyProfileRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        var callerUserId = Guid.Parse(User.FindFirst("sub")!.Value);
        ReplaceMyProfileResponse response = await _handler.HandleAsync(callerUserId, request, cancellationToken);
        return Ok(response);
    }
}
