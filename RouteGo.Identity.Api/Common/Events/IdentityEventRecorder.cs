namespace RouteGo.Identity.Api.Common.Events;

/// <summary>
/// Records an identity event inside the caller's write transaction, so the event exists exactly
/// when the change it describes commits. The background publisher delivers it later.
/// </summary>
public static class IdentityEventRecorder
{
    /// <summary>A customer or operator registered, or asked for a new verification link.</summary>
    public const string UserRegistered = "UserRegistered";

    /// <summary>An admin approved an operator application.</summary>
    public const string OperatorApproved = "OperatorApproved";

    /// <summary>An admin rejected an operator application.</summary>
    public const string OperatorRejected = "OperatorRejected";

    /// <summary>An auditable action happened; delivered to the audit stream.</summary>
    public const string AuditRecorded = "AuditRecorded";

    private const string IdentityEventsStream = "IdentityEvents";
    private const string AuditEventsStream = "AuditEvents";

    // clock_timestamp(), not now(): two events recorded in one transaction must still get distinct,
    // increasing times, because recorded_at is the delivery order.
    private const string InsertSql = """
        INSERT INTO authentication.identity_events (event_type, stream, data, correlation_id, recorded_at)
        VALUES (@EventType, @Stream, @Data, @CorrelationId, clock_timestamp())
        """;

    /// <summary>Records one event in the open write transaction.</summary>
    /// <param name="writer">The caller's open write transaction.</param>
    /// <param name="eventType">One of the event-type constants on this class.</param>
    /// <param name="data">The event's data, serialized with the wire JSON policy.</param>
    /// <param name="correlationId">The correlation ID of the request or command that caused it.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    public static async Task RecordAsync(
        IWriterConnection writer, string eventType, object data, string correlationId, CancellationToken cancellationToken)
    {
        string stream = eventType == AuditRecorded ? AuditEventsStream : IdentityEventsStream;
        string json = JsonSerializer.Serialize(data, data.GetType(), WireJson.Options);
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new { EventType = eventType, Stream = stream, Data = json, CorrelationId = correlationId },
            writer.Transaction,
            cancellationToken: cancellationToken));
    }
}
