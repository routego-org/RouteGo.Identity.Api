namespace RouteGo.Identity.Api.Features.Authentication.PublishIdentityEvents;

/// <summary>What one publishing run did.</summary>
/// <param name="DeliveredCount">Events delivered and marked delivered by this run.</param>
/// <param name="AlertedCount">Events this run raised the delay alert for.</param>
/// <param name="IsSkipped">True when another publisher held the lock, so this run did nothing.</param>
public sealed record PublishIdentityEventsResult(int DeliveredCount, int AlertedCount, bool IsSkipped);

/// <summary>
/// One publishing run: delivers undelivered events oldest first, stops at the first one that cannot
/// be delivered so later events never overtake it, and raises the delay alert once per event that
/// has waited longer than five minutes.
/// </summary>
public sealed class PublishIdentityEventsHandler
{
    /// <summary>The alert code a CloudWatch metric filter matches on.</summary>
    public const string PublishDelayedCode = "EVENT_PUBLISH_DELAYED";

    // The job: prefix keeps publisher traffic separable from user actions in the audit log.
    private const string AuditAction = "job:PublishIdentityEvents";
    private const string Producer = "identity-service";
    private const int EnvelopeVersion = 1;
    private const int BatchSize = 100;

    // One publisher at a time across every running instance: two concurrent runs could deliver
    // events out of order. The transaction-scoped lock is released when the run commits.
    private const long PublisherLockKey = 4_711_011;

    private const string TryLockSql = "SELECT pg_try_advisory_xact_lock(@Key)";

    private const string SelectUndeliveredSql = """
        SELECT event_id, event_type, stream, data, correlation_id, recorded_at
        FROM authentication.identity_events
        WHERE delivered_at IS NULL
        ORDER BY recorded_at, event_id
        LIMIT @BatchSize
        """;

    private const string MarkDeliveredSql = """
        UPDATE authentication.identity_events SET delivered_at = @DeliveredAt WHERE event_id = @EventId
        """;

    private const string SelectOverdueSql = """
        SELECT event_id, event_type, recorded_at
        FROM authentication.identity_events
        WHERE delivered_at IS NULL AND delay_alerted_at IS NULL AND recorded_at < @Threshold
        ORDER BY recorded_at, event_id
        """;

    private const string MarkAlertedSql = """
        UPDATE authentication.identity_events SET delay_alerted_at = @AlertedAt WHERE event_id = @EventId
        """;

    private static readonly TimeSpan _delayAlertAfter = TimeSpan.FromMinutes(5);

    private readonly IActorWriterConnectionFactory _writerFactory;
    private readonly IIdentityEventPublisher _publisher;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    public PublishIdentityEventsHandler(
        IActorWriterConnectionFactory writerFactory, IIdentityEventPublisher publisher, IClock clock, ILogger logger)
    {
        _writerFactory = writerFactory;
        _publisher = publisher;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Runs once; safe to run again at any time.</summary>
    /// <param name="cancellationToken">Stops the run; undelivered events wait for the next one.</param>
    /// <returns>What the run did.</returns>
    public async Task<PublishIdentityEventsResult> HandleAsync(CancellationToken cancellationToken)
    {
        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(ActorIdentity.System, AuditAction, cancellationToken);

        bool isLocked = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            TryLockSql, new { Key = PublisherLockKey }, writer.Transaction, cancellationToken: cancellationToken));
        if (!isLocked)
        {
            return new PublishIdentityEventsResult(0, 0, true);
        }

        int deliveredCount = await DeliverAsync(writer, cancellationToken);
        int alertedCount = await AlertOverdueAsync(writer, cancellationToken);
        await writer.CommitAsync(cancellationToken);

        if (deliveredCount > 0)
        {
            _logger.LogInformation("Delivered {DeliveredCount} identity events", deliveredCount);
        }

        return new PublishIdentityEventsResult(deliveredCount, alertedCount, false);
    }

    private async Task<int> DeliverAsync(IWriterConnection writer, CancellationToken cancellationToken)
    {
        IEnumerable<EventRow> events = await writer.Connection.QueryAsync<EventRow>(new CommandDefinition(
            SelectUndeliveredSql, new { BatchSize }, writer.Transaction, cancellationToken: cancellationToken));

        int deliveredCount = 0;
        foreach (EventRow row in events)
        {
            bool isDelivered = await TryPublishAsync(row, cancellationToken);
            if (!isDelivered)
            {
                break;
            }

            await writer.Connection.ExecuteAsync(new CommandDefinition(
                MarkDeliveredSql, new { DeliveredAt = _clock.UtcNow, row.EventId }, writer.Transaction,
                cancellationToken: cancellationToken));
            deliveredCount++;
        }

        return deliveredCount;
    }

    private async Task<bool> TryPublishAsync(EventRow row, CancellationToken cancellationToken)
    {
        // Building the envelope sits inside the hold-back too: an event that cannot be sent for any
        // reason must still leave the run alive, or the delay alert below would never fire for it.
        try
        {
            string envelope = BuildEnvelope(row);
            await _publisher.PublishAsync(row.Stream, row.EventType, envelope, cancellationToken);
            return true;
        }
        // Only our own shutdown ends the run. A messaging timeout also surfaces as a cancellation
        // exception, and it is an undeliverable event like any other.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Handled by holding the event back: it and every later event wait for the next run.
            _logger.LogWarning(
                exception, "Identity event {EventId} could not be delivered; later events wait behind it", row.EventId);
            return false;
        }
    }

    private async Task<int> AlertOverdueAsync(IWriterConnection writer, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        IEnumerable<OverdueRow> overdue = await writer.Connection.QueryAsync<OverdueRow>(new CommandDefinition(
            SelectOverdueSql, new { Threshold = now - _delayAlertAfter }, writer.Transaction,
            cancellationToken: cancellationToken));

        int alertedCount = 0;
        foreach (OverdueRow row in overdue)
        {
            _logger.LogError(
                "{Code}: identity event {EventId} of type {EventType} recorded at {RecordedAt} is still undelivered",
                PublishDelayedCode, row.EventId, row.EventType, row.RecordedAt);
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                MarkAlertedSql, new { AlertedAt = now, row.EventId }, writer.Transaction,
                cancellationToken: cancellationToken));
            alertedCount++;
        }

        return alertedCount;
    }

    private static string BuildEnvelope(EventRow row)
    {
        using var data = JsonDocument.Parse(row.Data);
        var envelope = new EventEnvelope
        {
            EventId = row.EventId,
            EventType = row.EventType,
            Version = EnvelopeVersion,
            OccurredAt = row.RecordedAt,
            CorrelationId = row.CorrelationId,
            Producer = Producer,
            Data = data.RootElement,
        };
        return JsonSerializer.Serialize(envelope, WireJson.Options);
    }

    private sealed class EventRow
    {
        public Guid EventId { get; init; }

        public string EventType { get; init; } = string.Empty;

        public string Stream { get; init; } = string.Empty;

        public string Data { get; init; } = string.Empty;

        public string CorrelationId { get; init; } = string.Empty;

        public DateTime RecordedAt { get; init; }
    }

    private sealed record OverdueRow(Guid EventId, string EventType, DateTime RecordedAt);

    private sealed class EventEnvelope
    {
        public required Guid EventId { get; init; }

        public required string EventType { get; init; }

        public required int Version { get; init; }

        public required DateTime OccurredAt { get; init; }

        public required string CorrelationId { get; init; }

        public required string Producer { get; init; }

        public required JsonElement Data { get; init; }
    }
}

/// <summary>
/// Runs the publisher every five seconds for as long as the API process runs. A failed run is
/// logged and the next one retries, so a messaging or database outage never stops the loop.
/// </summary>
public sealed class PublishIdentityEventsService : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    private readonly PublishIdentityEventsHandler _handler;
    private readonly ILogger<PublishIdentityEventsService> _logger;

    public PublishIdentityEventsService(
        IActorWriterConnectionFactory writerFactory, IIdentityEventPublisher publisher, IClock clock,
        ILoggerFactory loggerFactory)
    {
        _handler = new PublishIdentityEventsHandler(
            writerFactory, publisher, clock, loggerFactory.CreateLogger<PublishIdentityEventsHandler>());
        _logger = loggerFactory.CreateLogger<PublishIdentityEventsService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _handler.HandleAsync(stoppingToken);
        }
        // A timeout inside the run is a failed run, not a shutdown; letting it escape would stop the host.
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Identity event publishing run failed; the next run retries");
        }
    }
}
