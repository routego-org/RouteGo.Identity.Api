using RouteGo.Identity.Api.Common.Accounts;
using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.OperatorOnboarding.ApplyAsOperator;

/// <summary>A bus company's application: its sign-in account and its company details.</summary>
public sealed class ApplyAsOperatorRequest
{
    /// <summary>The operator's sign-in email; must not already have an account.</summary>
    public string? Email { get; init; }

    /// <summary>At least 10 characters, with at least one letter and one digit.</summary>
    public string? Password { get; init; }

    /// <summary>The applicant's own name, 2–100 characters.</summary>
    public string? FullName { get; init; }

    /// <summary>Optional Indian mobile number: 10 digits starting 6–9, optionally prefixed +91.</summary>
    public string? Phone { get; init; }

    /// <summary>2–150 characters.</summary>
    public string? CompanyName { get; init; }

    /// <summary>The company's business contact address; may differ from the sign-in email.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>The company's 15-character GSTIN, upper case.</summary>
    public string? Gstin { get; init; }
}

public enum ApplyAsOperatorOutcome
{
    Applied,
    EmailAlreadyRegistered,
    GstinAlreadyRegistered,
}

/// <summary>The outcome, with the new application on success.</summary>
/// <param name="Outcome">How the application ended.</param>
/// <param name="Application">The new application when created.</param>
public sealed record ApplyAsOperatorResult(ApplyAsOperatorOutcome Outcome, OperatorApplicationResponse? Application = null);

public sealed class ApplyAsOperatorValidator : AbstractValidator<ApplyAsOperatorRequest>
{
    public ApplyAsOperatorValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(AccountRules.MaxEmailLength);
        RuleFor(x => x.Password).Must(AccountRules.IsAcceptablePassword)
            .WithMessage("The password must be at least 10 characters and contain a letter and a digit.");
        RuleFor(x => x.FullName).Must(AccountRules.IsAcceptableFullName)
            .WithMessage("The full name must be 2 to 100 characters.");
        RuleFor(x => x.Phone).Must(AccountRules.IsAcceptablePhone).When(x => x.Phone != null)
            .WithMessage("The phone must be an Indian mobile number: 10 digits starting 6 to 9, optionally prefixed +91.");
        RuleFor(x => x.CompanyName).Must(OperatorApplicationRules.IsAcceptableCompanyName)
            .WithMessage("The company name must be 2 to 150 characters.");
        RuleFor(x => x.ContactEmail).NotEmpty().EmailAddress().MaximumLength(OperatorApplicationRules.MaxContactEmailLength);
        RuleFor(x => x.Gstin).Must(OperatorApplicationRules.IsAcceptableGstin)
            .WithMessage("The GSTIN must be 15 characters in the standard format, upper case.");
    }
}

public sealed class ApplyAsOperatorHandler
{
    private const string AuditAction = "ApplyAsOperator";

    // The unique email index makes "already registered" atomic: a concurrent applicant with the
    // same email inserts nothing and gets no row back.
    private const string InsertOperatorSql = """
        INSERT INTO authentication.users (user_id, email, password_hash, full_name, phone, role, status, failed_sign_in_count)
        VALUES (@UserId, @Email, @PasswordHash, @FullName, @Phone, @Role, @Status, 0)
        ON CONFLICT ((lower(email))) DO NOTHING
        RETURNING user_id
        """;

    // Likewise the GSTIN index: only pending and approved applications hold a GSTIN.
    private const string InsertApplicationSql = """
        INSERT INTO operator_onboarding.operator_applications
            (application_id, operator_id, company_name, contact_email, gstin, status, submitted_at)
        VALUES (@ApplicationId, @OperatorId, @CompanyName, @ContactEmail, @Gstin, @Status, now())
        ON CONFLICT (gstin) WHERE status IN ('PendingVerification', 'PendingApproval', 'Approved') DO NOTHING
        RETURNING application_id
        """;

    private readonly IActorWriterConnectionFactory _writerFactory;
    private readonly VerificationLinks _verificationLinks;

    public ApplyAsOperatorHandler(IActorWriterConnectionFactory writerFactory, VerificationLinks verificationLinks)
    {
        _writerFactory = writerFactory;
        _verificationLinks = verificationLinks;
    }

    /// <summary>
    /// Creates the operator account, its first application, its verification link and the
    /// UserRegistered event together, or nothing at all.
    /// </summary>
    public async Task<ApplyAsOperatorResult> HandleAsync(
        ApplyAsOperatorRequest request, string correlationId, CancellationToken cancellationToken)
    {
        var operatorId = Guid.NewGuid();

        // Nobody is signed in yet: the account being created is the actor its own rows are attributed to.
        var actor = new ActorIdentity(operatorId.ToString(), request.Email!, request.Email!);
        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(actor, AuditAction, cancellationToken);

        bool isAccountCreated = await TryInsertOperatorAsync(writer, operatorId, request, cancellationToken);
        if (!isAccountCreated)
        {
            return new ApplyAsOperatorResult(ApplyAsOperatorOutcome.EmailAlreadyRegistered);
        }

        Guid? applicationId = await TryInsertApplicationAsync(writer, operatorId, request, cancellationToken);
        if (applicationId is null)
        {
            return new ApplyAsOperatorResult(ApplyAsOperatorOutcome.GstinAlreadyRegistered);
        }

        IssuedVerificationLink link = await _verificationLinks.IssueAsync(writer, operatorId, cancellationToken);
        await RecordUserRegisteredAsync(writer, operatorId, request, link, correlationId, cancellationToken);
        IReadOnlyList<OperatorApplicationResponse> applications = await OperatorApplicationReader.ReadAsync(
            writer.Connection, writer.Transaction, new[] { applicationId.Value }, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new ApplyAsOperatorResult(ApplyAsOperatorOutcome.Applied, applications[0]);
    }

    private static async Task<bool> TryInsertOperatorAsync(
        IWriterConnection writer, Guid operatorId, ApplyAsOperatorRequest request, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            UserId = operatorId,
            request.Email,
            PasswordHash = PasswordHashing.Hash(request.Password!),
            request.FullName,
            Phone = AccountRules.NormalizePhone(request.Phone),
            Role = nameof(AccountRole.Operator),
            Status = nameof(AccountStatus.PendingVerification),
        };
        Guid? inserted = await writer.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            InsertOperatorSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        return inserted.HasValue;
    }

    private static async Task<Guid?> TryInsertApplicationAsync(
        IWriterConnection writer, Guid operatorId, ApplyAsOperatorRequest request, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            ApplicationId = Guid.NewGuid(),
            OperatorId = operatorId,
            request.CompanyName,
            request.ContactEmail,
            request.Gstin,
            Status = nameof(ApplicationStatus.PendingVerification),
        };
        return await writer.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            InsertApplicationSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task RecordUserRegisteredAsync(
        IWriterConnection writer, Guid operatorId, ApplyAsOperatorRequest request, IssuedVerificationLink link,
        string correlationId, CancellationToken cancellationToken)
    {
        // The same UserRegistered fields as customer registration; the role tells Notification
        // which verification email to send.
        var data = new
        {
            userId = operatorId,
            email = request.Email,
            name = request.FullName,
            role = nameof(AccountRole.Operator),
            verificationLink = link.Link,
        };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.UserRegistered, data, correlationId, cancellationToken);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "OperatorOnboarding")]
[Route("operators/applications")]
public sealed class ApplyAsOperatorController : ControllerBase
{
    private readonly ApplyAsOperatorHandler _handler;
    private readonly ApplyAsOperatorValidator _validator = new ApplyAsOperatorValidator();

    public ApplyAsOperatorController(IActorWriterConnectionFactory writerFactory, VerificationLinks verificationLinks)
    {
        _handler = new ApplyAsOperatorHandler(writerFactory, verificationLinks);
    }

    /// <summary>Apply to sell on RouteGo as an operator.</summary>
    /// <response code="201">The application was received; the applicant must verify their email.</response>
    /// <response code="400">Validation failed (RFC 7807 problem+json).</response>
    /// <response code="409">The email or the GSTIN is already registered (EMAIL_ALREADY_REGISTERED, GSTIN_ALREADY_REGISTERED).</response>
    [AllowAnonymous] // Public by design: a company applies before any account or token exists.
    [HttpPost]
    [ProducesResponseType(typeof(OperatorApplicationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApplyAsOperator(ApplyAsOperatorRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        ApplyAsOperatorResult result = await _handler.HandleAsync(request, correlationId, cancellationToken);
        switch (result.Outcome)
        {
            case ApplyAsOperatorOutcome.Applied:
                return StatusCode(StatusCodes.Status201Created, result.Application);
            case ApplyAsOperatorOutcome.EmailAlreadyRegistered:
                return ProblemResult.EmailAlreadyRegistered(this, $"An account already exists for {request.Email}.");
            case ApplyAsOperatorOutcome.GstinAlreadyRegistered:
                return ProblemResult.GstinAlreadyRegistered(this, $"GSTIN {request.Gstin} is already registered; contact support.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
