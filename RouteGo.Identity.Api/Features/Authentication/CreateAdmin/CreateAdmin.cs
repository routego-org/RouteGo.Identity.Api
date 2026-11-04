using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.CreateAdmin;

/// <summary>The new admin's details, chosen by the creating admin.</summary>
public sealed class CreateAdminRequest
{
    /// <summary>Sign-in email; compared case-insensitively and must not already have an account.</summary>
    public string? Email { get; init; }

    /// <summary>The initial password: at least 10 characters, with at least one letter and one digit.</summary>
    public string? Password { get; init; }

    /// <summary>2–100 characters.</summary>
    public string? FullName { get; init; }

    /// <summary>Optional Indian mobile number: 10 digits starting 6–9, optionally prefixed +91.</summary>
    public string? Phone { get; init; }
}

/// <summary>The new admin account's summary.</summary>
public sealed class CreateAdminResponse
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

public enum CreateAdminOutcome
{
    Created,
    EmailAlreadyRegistered,
}

/// <summary>The outcome, with the new account on success.</summary>
/// <param name="Outcome">How the creation ended.</param>
/// <param name="Response">The new account's summary when created.</param>
public sealed record CreateAdminResult(CreateAdminOutcome Outcome, CreateAdminResponse? Response = null);

/// <summary>The creating admin and the request that carried the creation.</summary>
/// <param name="AdminUserId">The signed-in admin, from the access token.</param>
/// <param name="CorrelationId">The request's correlation ID, carried onto the audit record.</param>
public sealed record CreateAdminCaller(Guid AdminUserId, string CorrelationId);

public sealed class CreateAdminValidator : AbstractValidator<CreateAdminRequest>
{
    public CreateAdminValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(AccountRules.MaxEmailLength);
        RuleFor(x => x.Password).Must(AccountRules.IsAcceptablePassword)
            .WithMessage("The password must be at least 10 characters and contain a letter and a digit.");
        RuleFor(x => x.FullName).Must(AccountRules.IsAcceptableFullName)
            .WithMessage("The full name must be 2 to 100 characters.");
        RuleFor(x => x.Phone).Must(AccountRules.IsAcceptablePhone).When(x => x.Phone != null)
            .WithMessage("The phone must be an Indian mobile number: 10 digits starting 6 to 9, optionally prefixed +91.");
    }
}

public sealed class CreateAdminHandler
{
    private const string AuditAction = "CreateAdmin";
    private const string AdminCreatedAction = "AdminCreated";
    private const string AccountEntity = "Account";
    private const string UniqueViolation = "23505";

    private const string EmailTakenSql = """
        SELECT EXISTS (SELECT 1 FROM authentication.users WHERE lower(email) = lower(@Email))
        """;

    // An admin created by an admin has no verification step: the email counts as verified now.
    private const string InsertAdminSql = """
        INSERT INTO authentication.users
            (user_id, email, password_hash, full_name, phone, role, status, email_verified_at, failed_sign_in_count)
        VALUES (@UserId, @Email, @PasswordHash, @FullName, @Phone, @Role, @Status, now(), 0)
        RETURNING created_at
        """;

    private readonly IWriterConnectionFactory _writerFactory;

    public CreateAdminHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>Creates an Active admin account and records its audit event in the same transaction.</summary>
    public async Task<CreateAdminResult> HandleAsync(
        CreateAdminRequest request, CreateAdminCaller caller, CancellationToken cancellationToken)
    {
        var account = new NewAdmin
        {
            UserId = Guid.NewGuid(),
            Email = request.Email!,
            FullName = request.FullName!,
            Phone = AccountRules.NormalizePhone(request.Phone),
        };

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool isTaken = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            EmailTakenSql, new { account.Email }, writer.Transaction, cancellationToken: cancellationToken));
        if (isTaken)
        {
            return new CreateAdminResult(CreateAdminOutcome.EmailAlreadyRegistered);
        }

        DateTime? createdAt = await TryInsertAsync(writer, account, request.Password!, cancellationToken);
        if (createdAt is null)
        {
            return new CreateAdminResult(CreateAdminOutcome.EmailAlreadyRegistered);
        }

        await RecordAdminCreatedAsync(writer, account, caller, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new CreateAdminResult(CreateAdminOutcome.Created, ToResponse(account, createdAt.Value));
    }

    // The check above answers the ordinary case; the unique index makes it atomic when two admins
    // create the same email at once.
    private static async Task<DateTime?> TryInsertAsync(
        IWriterConnection writer, NewAdmin account, string password, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            account.UserId,
            account.Email,
            PasswordHash = PasswordHashing.Hash(password),
            account.FullName,
            account.Phone,
            Role = nameof(AccountRole.Admin),
            Status = nameof(AccountStatus.Active),
        };
        try
        {
            return await writer.Connection.QuerySingleAsync<DateTime>(new CommandDefinition(
                InsertAdminSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        }
        catch (DbException exception) when (exception.SqlState == UniqueViolation)
        {
            return null;
        }
    }

    // Only the audit record: creating an admin notifies no service and sends no email.
    private static async Task RecordAdminCreatedAsync(
        IWriterConnection writer, NewAdmin account, CreateAdminCaller caller, CancellationToken cancellationToken)
    {
        var audit = new AuditRecordedData
        {
            ActorId = caller.AdminUserId.ToString(),
            ActorRole = nameof(AccountRole.Admin),
            Action = AdminCreatedAction,
            EntityType = AccountEntity,
            EntityId = account.UserId.ToString(),
            Details = new { email = account.Email },
        };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.AuditRecorded, audit, caller.CorrelationId, cancellationToken);
    }

    private static CreateAdminResponse ToResponse(NewAdmin account, DateTime createdAtUtc)
    {
        return new CreateAdminResponse
        {
            UserId = account.UserId,
            Email = account.Email,
            FullName = account.FullName,
            Phone = account.Phone,
            Role = AccountRole.Admin,
            Status = AccountStatus.Active,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc)),
        };
    }

    private sealed class NewAdmin
    {
        public required Guid UserId { get; init; }

        public required string Email { get; init; }

        public required string FullName { get; init; }

        public required string? Phone { get; init; }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Authentication")]
[Route("auth/admin/users")]
public sealed class CreateAdminController : ControllerBase
{
    private readonly CreateAdminHandler _handler;
    private readonly CreateAdminValidator _validator = new CreateAdminValidator();

    public CreateAdminController(IWriterConnectionFactory writerFactory)
    {
        _handler = new CreateAdminHandler(writerFactory);
    }

    /// <summary>Create another admin account (Admin only).</summary>
    /// <response code="201">The admin account was created and is Active.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an Admin (FORBIDDEN).</response>
    /// <response code="409">The email already has an account (EMAIL_ALREADY_REGISTERED).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    [ProducesResponseType(typeof(CreateAdminResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAdmin(CreateAdminRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        var caller = new CreateAdminCaller(
            Guid.Parse(User.FindFirst("sub")!.Value), CorrelationIdMiddleware.GetCorrelationId(HttpContext));
        CreateAdminResult result = await _handler.HandleAsync(request, caller, cancellationToken);
        switch (result.Outcome)
        {
            case CreateAdminOutcome.Created:
                return Created($"/auth/admin/users/{result.Response!.UserId}", result.Response);
            case CreateAdminOutcome.EmailAlreadyRegistered:
                return ProblemResult.EmailAlreadyRegistered(this, $"An account already exists for {request.Email}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
