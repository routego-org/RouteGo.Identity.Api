using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Common.OperatorApplications;

/// <summary>
/// Reads operator applications in the shape every operator-onboarding operation returns. Shared
/// because applying, re-applying, listing, approving and rejecting all answer with it, and the
/// earlier-rejections rule must read the same everywhere.
/// </summary>
public static class OperatorApplicationReader
{
    // Two statements, one round trip: the applications, then every earlier rejection of their
    // operators. "Earlier" means submitted before the application it is listed under.
    private const string SelectSql = """
        SELECT a.application_id, a.operator_id, u.email, u.full_name, a.company_name, a.contact_email,
               a.gstin, a.status, a.submitted_at, a.reviewed_at, a.reject_reason
        FROM operator_onboarding.operator_applications a
        JOIN authentication.users u ON u.user_id = a.operator_id
        WHERE a.application_id = ANY(@ApplicationIds);

        SELECT listed.application_id AS listed_under, earlier.application_id, earlier.submitted_at,
               earlier.reviewed_at AS rejected_at, earlier.reject_reason AS reason
        FROM operator_onboarding.operator_applications listed
        JOIN operator_onboarding.operator_applications earlier
          ON earlier.operator_id = listed.operator_id
         AND earlier.status = 'Rejected'
         AND earlier.submitted_at < listed.submitted_at
        WHERE listed.application_id = ANY(@ApplicationIds)
        ORDER BY earlier.reviewed_at DESC;
        """;

    /// <summary>Reads the given applications, in the order given.</summary>
    /// <param name="connection">An open connection; a writer's, to see its own uncommitted rows.</param>
    /// <param name="transaction">The writer's transaction, or null on a read connection.</param>
    /// <param name="applicationIds">The applications to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<OperatorApplicationResponse>> ReadAsync(
        DbConnection connection, DbTransaction? transaction, IReadOnlyList<Guid> applicationIds,
        CancellationToken cancellationToken)
    {
        var parameters = new { ApplicationIds = applicationIds.ToArray() };
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectSql, parameters, transaction, cancellationToken: cancellationToken));
        IEnumerable<ApplicationRow> applications = await results.ReadAsync<ApplicationRow>();
        IEnumerable<RejectionRow> rejections = await results.ReadAsync<RejectionRow>();

        var byId = applications.ToDictionary(row => row.ApplicationId);
        ILookup<Guid, RejectionRow> rejectionsByApplication = rejections.ToLookup(row => row.ListedUnder);

        var responses = new List<OperatorApplicationResponse>();
        foreach (Guid applicationId in applicationIds)
        {
            responses.Add(ToResponse(byId[applicationId], rejectionsByApplication[applicationId]));
        }

        return responses;
    }

    private static OperatorApplicationResponse ToResponse(ApplicationRow row, IEnumerable<RejectionRow> rejections)
    {
        var previous = new List<PreviousRejection>();
        foreach (RejectionRow rejection in rejections)
        {
            previous.Add(new PreviousRejection
            {
                ApplicationId = rejection.ApplicationId,
                SubmittedAt = AsUtc(rejection.SubmittedAt),
                RejectedAt = AsUtc(rejection.RejectedAt),
                Reason = rejection.Reason,
            });
        }

        DateTimeOffset? reviewedAt = null;
        if (row.ReviewedAt.HasValue)
        {
            reviewedAt = AsUtc(row.ReviewedAt.Value);
        }

        return new OperatorApplicationResponse
        {
            ApplicationId = row.ApplicationId,
            OperatorId = row.OperatorId,
            Email = row.Email,
            FullName = row.FullName,
            CompanyName = row.CompanyName,
            ContactEmail = row.ContactEmail,
            Gstin = row.Gstin,
            Status = Enum.Parse<ApplicationStatus>(row.Status),
            SubmittedAt = AsUtc(row.SubmittedAt),
            ReviewedAt = reviewedAt,
            RejectReason = row.RejectReason,
            PreviousRejections = previous,
        };
    }

    // timestamptz reads back as a UTC DateTime.
    private static DateTimeOffset AsUtc(DateTime utc)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed class ApplicationRow
    {
        public Guid ApplicationId { get; init; }

        public Guid OperatorId { get; init; }

        public string Email { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string CompanyName { get; init; } = string.Empty;

        public string ContactEmail { get; init; } = string.Empty;

        public string Gstin { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public DateTime SubmittedAt { get; init; }

        public DateTime? ReviewedAt { get; init; }

        public string? RejectReason { get; init; }
    }

    private sealed class RejectionRow
    {
        public Guid ListedUnder { get; init; }

        public Guid ApplicationId { get; init; }

        public DateTime SubmittedAt { get; init; }

        public DateTime RejectedAt { get; init; }

        public string Reason { get; init; } = string.Empty;
    }
}
