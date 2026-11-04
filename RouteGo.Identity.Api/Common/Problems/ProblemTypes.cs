namespace RouteGo.Identity.Api.Common.Problems;

/// <summary>
/// Projection of the product's problem-type catalog (contracts/problem-types.md in the
/// product-specs repo). Codes are never minted here — a new code starts life in the catalog
/// and lands here as a constant carrying the same code and the same type URI, so this class
/// can be diffed against the catalog rather than trusted to match it.
/// <para>
/// Seeded with every baseline code this codebase itself emits. The baseline rows that are
/// missing are emitted by middleware this project does not own — 401/403 by the authentication
/// handler, 429 by the rate limiter — and 400 validation problems are identified by their
/// <c>errors</c> member rather than a code, by contract. Append one constant per product code
/// as capabilities emit them (references/host-components.md).
/// </para>
/// </summary>
public static class ProblemTypes
{
    // The catalog declares a URN base and a ':' separator: urn:routego:identity:problems:<kebab-case>.
    private const string ProblemsBase = "urn:routego:identity:problems";

    /// <summary>401 — missing or invalid access token. Emitted by the bearer handler, never by a slice.</summary>
    public static readonly ProblemType Unauthorized = new("UNAUTHORIZED", $"{ProblemsBase}:unauthorized");

    /// <summary>403 — authenticated, but the role is not allowed. Emitted by the bearer handler, never by a slice.</summary>
    public static readonly ProblemType Forbidden = new("FORBIDDEN", $"{ProblemsBase}:forbidden");

    /// <summary>404 — the resource does not exist.</summary>
    public static readonly ProblemType NotFound = new("NOT_FOUND", $"{ProblemsBase}:not-found");

    /// <summary>409 — state changed since read, or the request duplicates existing state.</summary>
    public static readonly ProblemType Conflict = new("CONFLICT", $"{ProblemsBase}:conflict");

    /// <summary>422 — a business rule refused the request and no specific code exists yet.</summary>
    public static readonly ProblemType BusinessRuleViolated =
        new("BUSINESS_RULE_VIOLATED", $"{ProblemsBase}:business-rule-violated");

    /// <summary>502 — a required dependency is down or returned an unusable response.</summary>
    public static readonly ProblemType UpstreamUnavailable =
        new("UPSTREAM_UNAVAILABLE", $"{ProblemsBase}:upstream-unavailable");

    /// <summary>504 — a required dependency did not respond in time and no data could be served.</summary>
    public static readonly ProblemType UpstreamTimeout =
        new("UPSTREAM_TIMEOUT", $"{ProblemsBase}:upstream-timeout");

    /// <summary>500 — unhandled server fault. Emitted by UnhandledExceptionHandler, never by a slice.</summary>
    public static readonly ProblemType InternalError =
        new("INTERNAL_ERROR", $"{ProblemsBase}:internal-error");

    /// <summary>409 — registration, operator application or admin creation uses an email that already has an account.</summary>
    public static readonly ProblemType EmailAlreadyRegistered =
        new("EMAIL_ALREADY_REGISTERED", $"{ProblemsBase}:email-already-registered");

    /// <summary>422 — the verification link is more than 24 hours old.</summary>
    public static readonly ProblemType VerificationLinkExpired =
        new("VERIFICATION_LINK_EXPIRED", $"{ProblemsBase}:verification-link-expired");

    /// <summary>422 — the verification link was already used.</summary>
    public static readonly ProblemType VerificationLinkAlreadyUsed =
        new("VERIFICATION_LINK_ALREADY_USED", $"{ProblemsBase}:verification-link-already-used");

    /// <summary>422 — the link is unknown, or was replaced by a newer link.</summary>
    public static readonly ProblemType VerificationLinkInvalid =
        new("VERIFICATION_LINK_INVALID", $"{ProblemsBase}:verification-link-invalid");

    /// <summary>422 — resend verification for an email with no account.</summary>
    public static readonly ProblemType EmailNotRegistered =
        new("EMAIL_NOT_REGISTERED", $"{ProblemsBase}:email-not-registered");

    /// <summary>422 — resend verification for an account whose email is already verified.</summary>
    public static readonly ProblemType EmailAlreadyVerified =
        new("EMAIL_ALREADY_VERIFIED", $"{ProblemsBase}:email-already-verified");

    /// <summary>401 — email unknown, or password wrong.</summary>
    public static readonly ProblemType InvalidCredentials =
        new("INVALID_CREDENTIALS", $"{ProblemsBase}:invalid-credentials");

    /// <summary>403 — correct password, but the email is not verified yet.</summary>
    public static readonly ProblemType EmailNotVerified = new("EMAIL_NOT_VERIFIED", $"{ProblemsBase}:email-not-verified");

    /// <summary>403 — correct password, but the operator application is awaiting admin review.</summary>
    public static readonly ProblemType AccountPendingApproval =
        new("ACCOUNT_PENDING_APPROVAL", $"{ProblemsBase}:account-pending-approval");

    /// <summary>403 — correct password, but the operator application was rejected.</summary>
    public static readonly ProblemType AccountRejected = new("ACCOUNT_REJECTED", $"{ProblemsBase}:account-rejected");

    /// <summary>403 — correct password, but an admin has suspended the account.</summary>
    public static readonly ProblemType AccountSuspended = new("ACCOUNT_SUSPENDED", $"{ProblemsBase}:account-suspended");

    /// <summary>403 — 5 failed sign-ins within 10 minutes; locked for 15 minutes.</summary>
    public static readonly ProblemType AccountLocked = new("ACCOUNT_LOCKED", $"{ProblemsBase}:account-locked");

    /// <summary>401 — refresh token unknown, expired or revoked.</summary>
    public static readonly ProblemType RefreshTokenInvalid =
        new("REFRESH_TOKEN_INVALID", $"{ProblemsBase}:refresh-token-invalid");

    /// <summary>401 — an already-rotated refresh token was presented; its whole chain is now revoked.</summary>
    public static readonly ProblemType RefreshTokenReused =
        new("REFRESH_TOKEN_REUSED", $"{ProblemsBase}:refresh-token-reused");

    /// <summary>422 — an admin tried to suspend an Admin account.</summary>
    public static readonly ProblemType AdminCannotBeSuspended =
        new("ADMIN_CANNOT_BE_SUSPENDED", $"{ProblemsBase}:admin-cannot-be-suspended");

    /// <summary>409 — suspend requested for an account that is not Active.</summary>
    public static readonly ProblemType UserNotActive = new("USER_NOT_ACTIVE", $"{ProblemsBase}:user-not-active");

    /// <summary>409 — reactivate requested for an account that is not Suspended.</summary>
    public static readonly ProblemType UserNotSuspended = new("USER_NOT_SUSPENDED", $"{ProblemsBase}:user-not-suspended");

    /// <summary>409 — the GSTIN belongs to another operator's pending or approved application.</summary>
    public static readonly ProblemType GstinAlreadyRegistered =
        new("GSTIN_ALREADY_REGISTERED", $"{ProblemsBase}:gstin-already-registered");

    /// <summary>409 — re-application for an account that is not a rejected operator.</summary>
    public static readonly ProblemType OperatorNotRejected =
        new("OPERATOR_NOT_REJECTED", $"{ProblemsBase}:operator-not-rejected");

    /// <summary>422 — re-application within 7 days of the rejection.</summary>
    public static readonly ProblemType ReapplicationTooSoon =
        new("REAPPLICATION_TOO_SOON", $"{ProblemsBase}:reapplication-too-soon");

    /// <summary>409 — approve or reject an application that is not awaiting review.</summary>
    public static readonly ProblemType ApplicationNotPending =
        new("APPLICATION_NOT_PENDING", $"{ProblemsBase}:application-not-pending");

    /// <summary>429 — too many requests.</summary>
    public static readonly ProblemType RateLimited = new("RATE_LIMITED", $"{ProblemsBase}:rate-limited");
}
