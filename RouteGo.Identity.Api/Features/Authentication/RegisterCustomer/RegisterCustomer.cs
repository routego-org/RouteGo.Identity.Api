using RouteGo.Identity.Api.Common.Accounts;
using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.Authentication.RegisterCustomer;

/// <summary>A visitor's registration details.</summary>
public sealed class RegisterCustomerRequest
{
    /// <summary>Sign-in email; compared case-insensitively and must not already have an account.</summary>
    public string? Email { get; init; }

    /// <summary>At least 10 characters, with at least one letter and one digit.</summary>
    public string? Password { get; init; }

    /// <summary>2–100 characters.</summary>
    public string? FullName { get; init; }

    /// <summary>Optional Indian mobile number: 10 digits starting 6–9, optionally prefixed +91.</summary>
    public string? Phone { get; init; }
}

/// <summary>The new account's summary.</summary>
public sealed class RegisterCustomerResponse
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

public enum RegisterCustomerOutcome
{
    Created,
    EmailAlreadyRegistered,
}

/// <summary>The outcome, with the new account on success.</summary>
/// <param name="Outcome">How registration ended.</param>
/// <param name="Response">The new account's summary when created.</param>
public sealed record RegisterCustomerResult(RegisterCustomerOutcome Outcome, RegisterCustomerResponse? Response = null);

public sealed class RegisterCustomerValidator : AbstractValidator<RegisterCustomerRequest>
{
    public RegisterCustomerValidator()
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

public sealed class RegisterCustomerHandler
{
    private const string AuditAction = "RegisterCustomer";
    private const string UniqueViolation = "23505";

    private const string EmailTakenSql = """
        SELECT EXISTS (SELECT 1 FROM authentication.users WHERE lower(email) = lower(@Email))
        """;

    private const string InsertUserSql = """
        INSERT INTO authentication.users (user_id, email, password_hash, full_name, phone, role, status, failed_sign_in_count)
        VALUES (@UserId, @Email, @PasswordHash, @FullName, @Phone, @Role, @Status, 0)
        RETURNING created_at
        """;

    private readonly IActorWriterConnectionFactory _writerFactory;
    private readonly VerificationLinks _verificationLinks;

    public RegisterCustomerHandler(IActorWriterConnectionFactory writerFactory, VerificationLinks verificationLinks)
    {
        _writerFactory = writerFactory;
        _verificationLinks = verificationLinks;
    }

    public async Task<RegisterCustomerResult> HandleAsync(
        RegisterCustomerRequest request, string correlationId, CancellationToken cancellationToken)
    {
        var account = new NewAccount
        {
            UserId = Guid.NewGuid(),
            Email = request.Email!,
            FullName = request.FullName!,
            Phone = AccountRules.NormalizePhone(request.Phone),
            CorrelationId = correlationId,
        };

        // Nobody is signed in yet: the account being created is the actor its own rows are attributed to.
        var actor = new ActorIdentity(account.UserId.ToString(), account.Email, account.Email);
        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(actor, AuditAction, cancellationToken);

        bool isTaken = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            EmailTakenSql, new { account.Email }, writer.Transaction, cancellationToken: cancellationToken));
        if (isTaken)
        {
            return new RegisterCustomerResult(RegisterCustomerOutcome.EmailAlreadyRegistered);
        }

        DateTime? createdAt = await TryInsertAsync(writer, account, request.Password!, cancellationToken);
        if (createdAt is null)
        {
            return new RegisterCustomerResult(RegisterCustomerOutcome.EmailAlreadyRegistered);
        }

        IssuedVerificationLink link = await _verificationLinks.IssueAsync(writer, account.UserId, cancellationToken);
        await RecordUserRegisteredAsync(writer, account, link, cancellationToken);
        await writer.CommitAsync(cancellationToken);

        return new RegisterCustomerResult(RegisterCustomerOutcome.Created, ToResponse(account, createdAt.Value));
    }

    // The check above answers the ordinary case; the unique index makes it atomic. Two visitors
    // registering the same email at once both pass the check, and the second is the same refusal.
    private static async Task<DateTime?> TryInsertAsync(
        IWriterConnection writer, NewAccount account, string password, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            account.UserId,
            account.Email,
            PasswordHash = PasswordHashing.Hash(password),
            account.FullName,
            account.Phone,
            Role = nameof(AccountRole.Customer),
            Status = nameof(AccountStatus.PendingVerification),
        };
        try
        {
            return await writer.Connection.QuerySingleAsync<DateTime>(new CommandDefinition(
                InsertUserSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        }
        catch (DbException exception) when (exception.SqlState == UniqueViolation)
        {
            return null;
        }
    }

    private static async Task RecordUserRegisteredAsync(
        IWriterConnection writer, NewAccount account, IssuedVerificationLink link, CancellationToken cancellationToken)
    {
        // Field names are the RouteGo event catalog's (userId, email, name, role, verificationLink),
        // and the role keeps the system-wide spelling consumers share.
        var data = new
        {
            userId = account.UserId,
            email = account.Email,
            name = account.FullName,
            role = nameof(AccountRole.Customer),
            verificationLink = link.Link,
        };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.UserRegistered, data, account.CorrelationId, cancellationToken);
    }

    private static RegisterCustomerResponse ToResponse(NewAccount account, DateTime createdAtUtc)
    {
        return new RegisterCustomerResponse
        {
            UserId = account.UserId,
            Email = account.Email,
            FullName = account.FullName,
            Phone = account.Phone,
            Role = AccountRole.Customer,
            Status = AccountStatus.PendingVerification,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc)),
        };
    }

    private sealed class NewAccount
    {
        public required Guid UserId { get; init; }

        public required string Email { get; init; }

        public required string FullName { get; init; }

        public required string? Phone { get; init; }

        public required string CorrelationId { get; init; }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Authentication")]
[Route("auth/registrations")]
public sealed class RegisterCustomerController : ControllerBase
{
    private readonly RegisterCustomerHandler _handler;
    private readonly RegisterCustomerValidator _validator = new RegisterCustomerValidator();

    public RegisterCustomerController(IActorWriterConnectionFactory writerFactory, VerificationLinks verificationLinks)
    {
        _handler = new RegisterCustomerHandler(writerFactory, verificationLinks);
    }

    /// <summary>Register a new customer account.</summary>
    /// <response code="201">The account was created and the verification email is on its way.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="409">The email already has an account (EMAIL_ALREADY_REGISTERED).</response>
    [AllowAnonymous] // Public by design: a visitor registers before any account or token exists.
    [HttpPost]
    [ProducesResponseType(typeof(RegisterCustomerResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterCustomer(RegisterCustomerRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        RegisterCustomerResult result = await _handler.HandleAsync(request, correlationId, cancellationToken);
        switch (result.Outcome)
        {
            case RegisterCustomerOutcome.Created:
                return Created($"/auth/admin/users/{result.Response!.UserId}", result.Response);
            case RegisterCustomerOutcome.EmailAlreadyRegistered:
                return ProblemResult.EmailAlreadyRegistered(this, $"An account already exists for {request.Email}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
