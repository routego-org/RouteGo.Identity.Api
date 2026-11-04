namespace RouteGo.Identity.Api.Common.OperatorApplications;

/// <summary>An earlier application of the same operator that an admin rejected.</summary>
public sealed class PreviousRejection
{
    public required Guid ApplicationId { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public required DateTimeOffset RejectedAt { get; init; }

    public required string Reason { get; init; }
}
