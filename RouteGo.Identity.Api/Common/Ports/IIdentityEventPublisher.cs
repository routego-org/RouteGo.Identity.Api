namespace RouteGo.Identity.Api.Common.Ports;

/// <summary>
/// Delivers one recorded identity event to its stream. A port so the event publisher's ordering,
/// retry and alerting rules can be tested without the messaging service.
/// </summary>
public interface IIdentityEventPublisher
{
    /// <summary>Delivers the event; throws when the messaging service does not accept it.</summary>
    /// <param name="stream">The stream recorded with the event: IdentityEvents or AuditEvents.</param>
    /// <param name="eventType">The event type, exposed as a message attribute consumers filter on.</param>
    /// <param name="envelopeJson">The complete RouteGo event envelope.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    Task PublishAsync(string stream, string eventType, string envelopeJson, CancellationToken cancellationToken);
}
