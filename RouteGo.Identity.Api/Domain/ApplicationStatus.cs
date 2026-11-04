namespace RouteGo.Identity.Api.Domain;

/// <summary>
/// Where one operator application is in review. PendingVerification only until the applicant
/// verifies their email (first application only); Approved and Rejected never change again.
/// </summary>
public enum ApplicationStatus
{
    PendingVerification,
    PendingApproval,
    Approved,
    Rejected,
}
