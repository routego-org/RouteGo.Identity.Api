using System.Text.Json;
using Microsoft.Extensions.Logging;
using RouteGo.Identity.Api.Features.Authentication.PublishIdentityEvents;
using RouteGo.Identity.Api.Infrastructure.Pgsql;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.PublishIdentityEvents;

[Collection(DatabaseCollection.Name)]
public sealed class PublishIdentityEventsTests : IAsyncLifetime
{
    private const string InsertEventSql = """
        INSERT INTO authentication.identity_events (event_id, event_type, stream, data, correlation_id, recorded_at)
        VALUES (@EventId, @EventType, @Stream, @Data, @CorrelationId, @RecordedAt)
        """;

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid _first = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid _second = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid _third = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private readonly DatabaseFixture _database;
    private readonly RecordingEventPublisher _publisher = new RecordingEventPublisher();
    private readonly CapturingLogger _logger = new CapturingLogger();
    private readonly PublishIdentityEventsHandler _handler;

    public PublishIdentityEventsTests(DatabaseFixture database)
    {
        _database = database;
        var writerFactory = new ActorWriterConnectionFactory(database.ConnectionString, ConnectionRetryOptions.ManagedInstances);
        _handler = new PublishIdentityEventsHandler(writerFactory, _publisher, new FixedClock(_now), _logger);
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleAsync_RecordedEvent_DeliversEnvelopeAndMarksDelivered()
    {
        // Arrange
        await SeedEventAsync(_first, "UserRegistered", _now.AddSeconds(-3));

        // Act
        PublishIdentityEventsResult result = await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, result.DeliveredCount);
        PublishedEvent published = Assert.Single(_publisher.Published);
        Assert.Equal("IdentityEvents", published.Stream);
        using var envelope = JsonDocument.Parse(published.Envelope);
        Assert.Equal(_first, envelope.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal("UserRegistered", envelope.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(1, envelope.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("corr-" + _first, envelope.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("identity-service", envelope.RootElement.GetProperty("producer").GetString());
        Assert.Equal("alice@example.com", envelope.RootElement.GetProperty("data").GetProperty("email").GetString());
        Assert.Equal(_now, await GetDeliveredAtAsync(_first));
    }

    [Fact]
    public async Task HandleAsync_AuditRecordedEvent_GoesToTheAuditStream()
    {
        // Arrange
        await SeedEventAsync(_first, "AuditRecorded", _now.AddSeconds(-3));

        // Act
        await _handler.HandleAsync(CancellationToken.None);

        // Assert
        PublishedEvent published = Assert.Single(_publisher.Published);
        Assert.Equal("AuditEvents", published.Stream);
        Assert.Equal("AuditRecorded", published.EventType);
    }

    [Fact]
    public async Task HandleAsync_ThreeUndeliveredEvents_DeliversThemInRecordedOrder()
    {
        // Arrange
        await SeedEventAsync(_third, "OperatorRejected", _now.AddSeconds(-1));
        await SeedEventAsync(_first, "UserRegistered", _now.AddSeconds(-3));
        await SeedEventAsync(_second, "OperatorApproved", _now.AddSeconds(-2));

        // Act
        await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(
            new[] { "UserRegistered", "OperatorApproved", "OperatorRejected" },
            _publisher.Published.Select(published => published.EventType));
    }

    [Fact]
    public async Task HandleAsync_EventAlreadyDelivered_IsNotDeliveredAgain()
    {
        // Arrange
        await SeedEventAsync(_first, "UserRegistered", _now.AddSeconds(-3));
        await _handler.HandleAsync(CancellationToken.None);

        // Act
        PublishIdentityEventsResult second = await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, second.DeliveredCount);
        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task HandleAsync_MessagingUnavailable_KeepsEventsThenDeliversInOrderOnRecovery()
    {
        // Arrange
        await SeedEventAsync(_first, "UserRegistered", _now.AddSeconds(-3));
        await SeedEventAsync(_second, "OperatorApproved", _now.AddSeconds(-2));
        _publisher.IsUnavailable = true;
        PublishIdentityEventsResult duringOutage = await _handler.HandleAsync(CancellationToken.None);
        _publisher.IsUnavailable = false;

        // Act
        PublishIdentityEventsResult afterRecovery = await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, duringOutage.DeliveredCount);
        Assert.Equal(2, afterRecovery.DeliveredCount);
        Assert.Equal(
            new[] { "UserRegistered", "OperatorApproved" },
            _publisher.Published.Select(published => published.EventType));
    }

    [Fact]
    public async Task HandleAsync_MessagingTimesOut_KeepsTheEventAndTheRunCompletes()
    {
        // Arrange
        await SeedEventAsync(_first, "UserRegistered", _now.AddSeconds(-3));
        _publisher.IsTimingOut = true;

        // Act
        PublishIdentityEventsResult result = await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, result.DeliveredCount);
        Assert.Null(await GetDeliveredAtAsync(_first));
    }

    [Fact]
    public async Task HandleAsync_DeliveryConfirmationLost_RedeliversWithTheSameEventId()
    {
        // Arrange: the first run delivered the event, then its confirmation was lost.
        await SeedEventAsync(_first, "UserRegistered", _now.AddSeconds(-3));
        await _handler.HandleAsync(CancellationToken.None);
        await _database.SeedAsync(
            "UPDATE authentication.identity_events SET delivered_at = NULL WHERE event_id = @EventId",
            new { EventId = _first });

        // Act
        await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, _publisher.Published.Count);
        Assert.Equal(_publisher.Published[0].Envelope, _publisher.Published[1].Envelope);
    }

    [Fact]
    public async Task HandleAsync_EventUndeliveredForOverFiveMinutes_RaisesTheAlertOnce()
    {
        // Arrange
        await SeedEventAsync(_first, "UserRegistered", _now.AddMinutes(-5).AddSeconds(-1));
        _publisher.IsUnavailable = true;

        // Act
        PublishIdentityEventsResult firstRun = await _handler.HandleAsync(CancellationToken.None);
        PublishIdentityEventsResult secondRun = await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, firstRun.AlertedCount);
        Assert.Equal(0, secondRun.AlertedCount);
        CapturedLog alert = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("EVENT_PUBLISH_DELAYED", alert.Message);
        Assert.Contains(_first.ToString(), alert.Message);
        Assert.Contains("UserRegistered", alert.Message);
    }

    [Fact]
    public async Task HandleAsync_EventUndeliveredForUnderFiveMinutes_RaisesNoAlert()
    {
        // Arrange
        await SeedEventAsync(_first, "UserRegistered", _now.AddMinutes(-4));
        _publisher.IsUnavailable = true;

        // Act
        PublishIdentityEventsResult result = await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, result.AlertedCount);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task HandleAsync_EventThatCannotBeSent_HoldsLaterEventsAndStillRaisesTheAlert()
    {
        // Arrange
        await SeedEventAsync(_first, "UserRegistered", _now.AddMinutes(-6));
        await _database.SeedAsync(
            "UPDATE authentication.identity_events SET data = 'not json' WHERE event_id = @EventId",
            new { EventId = _first });
        await SeedEventAsync(_second, "OperatorApproved", _now.AddSeconds(-2));

        // Act
        PublishIdentityEventsResult result = await _handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, result.DeliveredCount);
        Assert.Equal(1, result.AlertedCount);
        Assert.Empty(_publisher.Published);
    }

    private async Task SeedEventAsync(Guid eventId, string eventType, DateTimeOffset recordedAt)
    {
        string stream = eventType == "AuditRecorded" ? "AuditEvents" : "IdentityEvents";
        await _database.SeedAsync(InsertEventSql, new
        {
            EventId = eventId,
            EventType = eventType,
            Stream = stream,
            Data = """{"email":"alice@example.com"}""",
            CorrelationId = "corr-" + eventId,
            RecordedAt = recordedAt,
        });
    }

    private async Task<DateTimeOffset?> GetDeliveredAtAsync(Guid eventId)
    {
        DateTime? deliveredAt = await _database.QueryScalarAsync<DateTime?>(
            "SELECT delivered_at FROM authentication.identity_events WHERE event_id = @EventId", new { EventId = eventId });
        if (deliveredAt is null)
        {
            return null;
        }

        return new DateTimeOffset(deliveredAt.Value, TimeSpan.Zero);
    }
}
