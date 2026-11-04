using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Common.OperatorApplications;

/// <summary>
/// One operator application as every operator-onboarding operation returns it: the company
/// details, where it is in review, and the operator's earlier rejections, newest first.
/// </summary>
public sealed class OperatorApplicationResponse
{
    public required Guid ApplicationId { get; init; }

    /// <summary>The operator account's identifier (also the operatorId token claim).</summary>
    public required Guid OperatorId { get; init; }

    /// <summary>The operator's sign-in email.</summary>
    public required string Email { get; init; }

    public required string FullName { get; init; }

    public required string CompanyName { get; init; }

    public required string ContactEmail { get; init; }

    public required string Gstin { get; init; }

    public required ApplicationStatus Status { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public required DateTimeOffset? ReviewedAt { get; init; }

    public required string? RejectReason { get; init; }

    /// <summary>Earlier rejected applications of the same operator, newest first.</summary>
    public required IReadOnlyList<PreviousRejection> PreviousRejections { get; init; }
}
