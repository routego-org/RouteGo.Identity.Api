namespace RouteGo.Identity.Api.Common.Problems;

/// <summary>
/// An RFC 7807 problem response reporting one cataloged problem type. Every business refusal
/// in every slice is built here, so the same refusal always carries the same type URI, title,
/// status, and machine-readable code — and the catalog row stays inspectable, which is what
/// lets the codebase be checked against the product's problem-type catalog rather than
/// trusted to match it.
/// <para>
/// Extending this: one factory method per row in the product's problem-type catalog, and
/// never a method for a code the catalog does not define. The <c>detail</c> sentence is
/// always a parameter — it is the one part the catalog leaves to the operation — and no
/// slice-specific logic belongs here. The constructor is private so a problem response can
/// only ever be created through a cataloged factory.
/// </para>
/// </summary>
public sealed class ProblemResult : ObjectResult
{
    private readonly ProblemDetails _problemDetails;

    private ProblemResult(ProblemDetails problemDetails, ProblemType problemType)
        : base(problemDetails)
    {
        _problemDetails = problemDetails;
        ProblemType = problemType;
        StatusCode = problemDetails.Status;
    }

    /// <summary>The catalog row this response reports. Clients switch on its code.</summary>
    public ProblemType ProblemType { get; }

    /// <summary>404 — the resource does not exist.</summary>
    public static ProblemResult NotFound(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.NotFound, "The requested resource does not exist.",
            StatusCodes.Status404NotFound, detail);
    }

    /// <summary>409 — the request conflicts with existing state.</summary>
    public static ProblemResult Conflict(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.Conflict, "The request conflicts with existing state.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>422 — a business rule refused an otherwise well-formed request.</summary>
    public static ProblemResult BusinessRuleViolated(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.BusinessRuleViolated, "A business rule refused the request.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>
    /// 502 — a required dependency is down or returned an unusable response, leaving no usable
    /// data. A fan-out that got data from at least one source is a SUCCESS: return 200 with the
    /// contract's partial-success fields instead of a problem.
    /// </summary>
    public static ProblemResult UpstreamUnavailable(ControllerBase controller, string detail, bool retryable)
    {
        ProblemResult result = Build(controller, ProblemTypes.UpstreamUnavailable,
            "A required upstream service is unavailable.",
            StatusCodes.Status502BadGateway, detail);
        result.AddExtension("retryable", retryable);
        return result;
    }

    /// <summary>
    /// 504 — a required dependency did not respond in time and no data could be served. Pass
    /// retryAfterSeconds when the delay is known; the catalog documents it as a member clients
    /// act on. Same partial-success rule as UpstreamUnavailable.
    /// </summary>
    public static ProblemResult UpstreamTimeout(
        ControllerBase controller, string detail, bool retryable, int? retryAfterSeconds)
    {
        ProblemResult result = Build(controller, ProblemTypes.UpstreamTimeout,
            "A required upstream service did not respond in time.",
            StatusCodes.Status504GatewayTimeout, detail);
        result.AddExtension("retryable", retryable);
        if (retryAfterSeconds.HasValue)
        {
            result.AddExtension("retryAfterSeconds", retryAfterSeconds.Value);
        }

        return result;
    }

    /// <summary>409 — the email already has an account, of any role.</summary>
    public static ProblemResult EmailAlreadyRegistered(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.EmailAlreadyRegistered, "This email is already registered.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>422 — the verification link has expired.</summary>
    public static ProblemResult VerificationLinkExpired(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.VerificationLinkExpired, "This verification link has expired.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>422 — the verification link was already used; accountStatus says where the account now is.</summary>
    public static ProblemResult VerificationLinkAlreadyUsed(ControllerBase controller, string detail, string accountStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.VerificationLinkAlreadyUsed, "This email is already verified.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("accountStatus", accountStatus);
        return result;
    }

    /// <summary>422 — the verification link is unknown or was replaced by a newer one.</summary>
    public static ProblemResult VerificationLinkInvalid(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.VerificationLinkInvalid, "This verification link is not valid.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>422 — no account uses this email.</summary>
    public static ProblemResult EmailNotRegistered(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.EmailNotRegistered, "No account uses this email.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>422 — the email is already verified; accountStatus says where the account now is.</summary>
    public static ProblemResult EmailAlreadyVerified(ControllerBase controller, string detail, string accountStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.EmailAlreadyVerified, "This email is already verified.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("accountStatus", accountStatus);
        return result;
    }

    /// <summary>401 — the email or password is incorrect (never says which).</summary>
    public static ProblemResult InvalidCredentials(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.InvalidCredentials, "The email or password is incorrect.",
            StatusCodes.Status401Unauthorized, detail);
    }

    /// <summary>403 — the email must be verified before signing in.</summary>
    public static ProblemResult EmailNotVerified(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.EmailNotVerified, "Please verify your email before signing in.",
            StatusCodes.Status403Forbidden, detail);
    }

    /// <summary>403 — the operator application is awaiting review.</summary>
    public static ProblemResult AccountPendingApproval(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.AccountPendingApproval, "Your operator application is awaiting approval.",
            StatusCodes.Status403Forbidden, detail);
    }

    /// <summary>403 — the operator application was rejected.</summary>
    public static ProblemResult AccountRejected(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.AccountRejected, "Your operator application was rejected.",
            StatusCodes.Status403Forbidden, detail);
    }

    /// <summary>403 — an admin has suspended the account.</summary>
    public static ProblemResult AccountSuspended(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.AccountSuspended, "This account is suspended.",
            StatusCodes.Status403Forbidden, detail);
    }

    /// <summary>403 — the account is temporarily locked; lockedUntil says when it unlocks.</summary>
    public static ProblemResult AccountLocked(ControllerBase controller, string detail, DateTimeOffset lockedUntil)
    {
        ProblemResult result = Build(controller, ProblemTypes.AccountLocked, "This account is temporarily locked.",
            StatusCodes.Status403Forbidden, detail);
        result.AddExtension("lockedUntil", lockedUntil);
        return result;
    }

    /// <summary>401 — the refresh token cannot be used; the person signs in again.</summary>
    public static ProblemResult RefreshTokenInvalid(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.RefreshTokenInvalid, "Your session has ended. Please sign in again.",
            StatusCodes.Status401Unauthorized, detail);
    }

    /// <summary>401 — an already-used refresh token was presented and its chain was revoked.</summary>
    public static ProblemResult RefreshTokenReused(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.RefreshTokenReused, "Your session has ended. Please sign in again.",
            StatusCodes.Status401Unauthorized, detail);
    }

    /// <summary>422 — Admin accounts cannot be suspended.</summary>
    public static ProblemResult AdminCannotBeSuspended(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.AdminCannotBeSuspended, "Admin accounts cannot be suspended.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>409 — only an Active account can be suspended; currentStatus says where it is now.</summary>
    public static ProblemResult UserNotActive(ControllerBase controller, string detail, string currentStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.UserNotActive, "This account is not active.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("currentStatus", currentStatus);
        return result;
    }

    /// <summary>409 — only a Suspended account can be reactivated; currentStatus says where it is now.</summary>
    public static ProblemResult UserNotSuspended(ControllerBase controller, string detail, string currentStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.UserNotSuspended, "This account is not suspended.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("currentStatus", currentStatus);
        return result;
    }

    /// <summary>409 — another operator's pending or approved application holds this GSTIN.</summary>
    public static ProblemResult GstinAlreadyRegistered(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.GstinAlreadyRegistered, "This GSTIN is already registered with RouteGo.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>409 — only a rejected operator can re-apply; currentStatus says where the account is now.</summary>
    public static ProblemResult OperatorNotRejected(ControllerBase controller, string detail, string currentStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.OperatorNotRejected,
            "Only a rejected operator application can be resubmitted.", StatusCodes.Status409Conflict, detail);
        result.AddExtension("currentStatus", currentStatus);
        return result;
    }

    /// <summary>422 — the re-application wait has not passed; reapplyAllowedAt says when it does.</summary>
    public static ProblemResult ReapplicationTooSoon(ControllerBase controller, string detail, DateTimeOffset reapplyAllowedAt)
    {
        ProblemResult result = Build(controller, ProblemTypes.ReapplicationTooSoon,
            "You can re-apply 7 days after the rejection.", StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("reapplyAllowedAt", reapplyAllowedAt);
        return result;
    }

    /// <summary>409 — the application is not awaiting review; currentStatus says where it is now.</summary>
    public static ProblemResult ApplicationNotPending(ControllerBase controller, string detail, string currentStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.ApplicationNotPending,
            "This application is not awaiting review.", StatusCodes.Status409Conflict, detail);
        result.AddExtension("currentStatus", currentStatus);
        return result;
    }

    /// <summary>429 — too many requests; retryAfterSeconds says when to try again.</summary>
    public static ProblemResult RateLimited(ControllerBase controller, string detail, int retryAfterSeconds)
    {
        ProblemResult result = Build(controller, ProblemTypes.RateLimited, "Too many requests.",
            StatusCodes.Status429TooManyRequests, detail);
        result.AddExtension("retryAfterSeconds", retryAfterSeconds);
        return result;
    }

    /// <summary>
    /// Adds one extension member. Every member added here must appear in the catalog row's
    /// Extensions column — a member no consumer was told about is a member no consumer reads.
    /// </summary>
    private void AddExtension(string name, object value)
    {
        _problemDetails.Extensions[name] = value;
    }

    private static ProblemResult Build(
        ControllerBase controller, ProblemType problemType, string title, int statusCode, string detail)
    {
        // Built through the controller's ProblemDetailsFactory rather than by hand, because
        // that factory is what applies the host's customization — the traceId correlating this
        // response with server-side telemetry. A hand-constructed ProblemDetails loses it
        // silently, and the loss only shows up when someone tries to trace a reported error.
        ProblemDetails problemDetails = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext,
            statusCode: statusCode,
            title: title,
            type: problemType.Type,
            detail: detail);
        problemDetails.Extensions["code"] = problemType.Code;
        return new ProblemResult(problemDetails, problemType);
    }
}
