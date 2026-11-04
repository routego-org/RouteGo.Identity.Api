using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.OperatorOnboarding.ReapplyAsOperator;

/// <summary>The statements re-application runs, and the rows it reads.</summary>
public sealed partial class ReapplyAsOperatorHandler
{
    private const string SelectAccountSql = """
        SELECT user_id, email FROM authentication.users WHERE lower(email) = lower(@Email)
        """;

    // The row lock serializes this with sign-in attempts and with an admin's decision on the same
    // operator, so failures are counted exactly and the status read here is the one acted on.
    private const string SelectAccountForUpdateSql = """
        SELECT user_id, password_hash, role, status, failed_sign_in_count, first_failed_sign_in_at,
               locked_until, now() AS now
        FROM authentication.users
        WHERE user_id = @UserId
        FOR UPDATE
        """;

    private const string UpdateLockoutSql = """
        UPDATE authentication.users
        SET failed_sign_in_count = @FailedCount, first_failed_sign_in_at = @FirstFailedAt, locked_until = @LockedUntil
        WHERE user_id = @UserId
        """;

    // A Rejected operator's latest application is the rejected one; its decision time starts the wait.
    private const string SelectLatestRejectionSql = """
        SELECT reviewed_at
        FROM operator_onboarding.operator_applications
        WHERE operator_id = @OperatorId
        ORDER BY submitted_at DESC
        LIMIT 1
        """;

    // The operator's own rejected application no longer holds its GSTIN, so re-using it is allowed;
    // only another operator's pending or approved application conflicts.
    private const string InsertApplicationSql = """
        INSERT INTO operator_onboarding.operator_applications
            (application_id, operator_id, company_name, contact_email, gstin, status, submitted_at)
        VALUES (@ApplicationId, @OperatorId, @CompanyName, @ContactEmail, @Gstin, @Status, now())
        ON CONFLICT (gstin) WHERE status IN ('PendingVerification', 'PendingApproval', 'Approved') DO NOTHING
        RETURNING application_id
        """;

    // Proving the password is a successful attempt, as at sign-in: the failure window clears.
    private const string SubmitOperatorForReviewSql = """
        UPDATE authentication.users
        SET status = 'PendingApproval', failed_sign_in_count = 0, first_failed_sign_in_at = NULL, locked_until = NULL
        WHERE user_id = @UserId
        """;

    private sealed record AccountKey(Guid UserId, string Email);

    private sealed class AccountRow
    {
        public Guid UserId { get; init; }

        public string PasswordHash { get; init; } = string.Empty;

        public string Role { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public int FailedSignInCount { get; init; }

        public DateTime? FirstFailedSignInAt { get; init; }

        public DateTime? LockedUntil { get; init; }

        public DateTime Now { get; init; }

        public LockoutState ToLockout()
        {
            DateTimeOffset? firstFailedAt = null;
            if (FirstFailedSignInAt.HasValue)
            {
                firstFailedAt = new DateTimeOffset(DateTime.SpecifyKind(FirstFailedSignInAt.Value, DateTimeKind.Utc));
            }

            DateTimeOffset? lockedUntil = null;
            if (LockedUntil.HasValue)
            {
                lockedUntil = new DateTimeOffset(DateTime.SpecifyKind(LockedUntil.Value, DateTimeKind.Utc));
            }

            return new LockoutState(FailedSignInCount, firstFailedAt, lockedUntil);
        }
    }
}
