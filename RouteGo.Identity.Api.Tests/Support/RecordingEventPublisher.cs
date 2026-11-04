using RouteGo.Identity.Api.Common.Ports;

namespace RouteGo.Identity.Api.Tests.Support;

/// <summary>One delivery the publisher accepted.</summary>
/// <param name="Stream">The stream it went to.</param>
/// <param name="EventType">The event type message attribute.</param>
/// <param name="Envelope">The envelope JSON.</param>
public sealed record PublishedEvent(string Stream, string EventType, string Envelope);

/// <summary>
/// Records every delivery instead of sending it; set <see cref="IsUnavailable"/> to simulate a
/// messaging outage.
/// </summary>
public sealed class RecordingEventPublisher : IIdentityEventPublisher
{
    private readonly List<PublishedEvent> _published = new List<PublishedEvent>();

    /// <summary>When true, every delivery fails as if the messaging service were down.</summary>
    public bool IsUnavailable { get; set; }

    /// <summary>When true, every delivery times out the way the HTTP client reports it: as a cancellation.</summary>
    public bool IsTimingOut { get; set; }

    /// <summary>Every accepted delivery, in order.</summary>
    public IReadOnlyList<PublishedEvent> Published
    {
        get
        {
            return _published;
        }
    }

    public Task PublishAsync(string stream, string eventType, string envelopeJson, CancellationToken cancellationToken)
    {
        if (IsUnavailable)
        {
            throw new HttpRequestException("The messaging service is unavailable.");
        }

        if (IsTimingOut)
        {
            throw new TaskCanceledException("The request to the messaging service timed out.");
        }

        _published.Add(new PublishedEvent(stream, eventType, envelopeJson));
        return Task.CompletedTask;
    }
}
