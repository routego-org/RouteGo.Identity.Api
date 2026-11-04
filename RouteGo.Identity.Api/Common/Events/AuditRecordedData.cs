namespace RouteGo.Identity.Api.Common.Events;

/// <summary>The data of an AuditRecorded event: who did what to which entity.</summary>
public sealed class AuditRecordedData
{
    /// <summary>The acting account's identifier, or the platform engineer's name for a key rotation.</summary>
    public required string ActorId { get; init; }

    /// <summary>Admin, or PlatformEngineer for a key rotation.</summary>
    public required string ActorRole { get; init; }

    /// <summary>The audited action, for example SigningKeyIntroduced.</summary>
    public required string Action { get; init; }

    /// <summary>What kind of entity it acted on, for example SigningKey.</summary>
    public required string EntityType { get; init; }

    /// <summary>That entity's identifier.</summary>
    public required string EntityId { get; init; }

    /// <summary>Action-specific details, serialized as a JSON object.</summary>
    public required object Details { get; init; }
}
