namespace RouteGo.Identity.Api.Domain;

/// <summary>Where an account is in its lifecycle. Only Active accounts may sign in.</summary>
public enum AccountStatus
{
    PendingVerification,
    PendingApproval,
    Active,
    Rejected,
    Suspended,
}
