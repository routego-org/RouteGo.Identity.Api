namespace RouteGo.Identity.Api.Common.Ports;

/// <summary>
/// The write path for work that has no signed-in principal — registration, sign-in, email
/// verification, the event publisher, the command-line tools. The caller names the actor
/// explicitly, so every write is still attributed and audited; there is no implicit fallback.
/// Signed-in writes keep using <see cref="IWriterConnectionFactory"/>.
/// </summary>
public interface IActorWriterConnectionFactory
{
    /// <summary>Opens a connection with a live transaction and the actor and action stamped.</summary>
    /// <param name="actor">Who the write is attributed to.</param>
    /// <param name="action">The business action, for example "RegisterCustomer".</param>
    /// <param name="cancellationToken">Forwarded to the open and stamp round trips.</param>
    Task<IWriterConnection> OpenWriteTransactionAsync(
        ActorIdentity actor, string action, CancellationToken cancellationToken = default);
}
