namespace RouteGo.Identity.Api.Infrastructure.Pgsql;

/// <summary>
/// Opens writer connections attributed to an actor the caller names, by building the standard
/// <see cref="WriterConnectionFactory"/> over that actor for one transaction. Reusing the standard
/// factory keeps one write path: the same stamping, retry and validation of the actor id.
/// </summary>
public sealed class ActorWriterConnectionFactory : IActorWriterConnectionFactory
{
    private readonly string _connectionString;
    private readonly ConnectionRetryOptions _retry;

    public ActorWriterConnectionFactory(string connectionString, ConnectionRetryOptions retry)
    {
        _connectionString = connectionString;
        _retry = retry;
    }

    /// <inheritdoc />
    public async Task<IWriterConnection> OpenWriteTransactionAsync(
        ActorIdentity actor, string action, CancellationToken cancellationToken = default)
    {
        var writer = new WriterConnectionFactory(_connectionString, new NamedActor(actor), _retry);
        return await writer.OpenWriteTransactionAsync(action, cancellationToken);
    }

    private sealed class NamedActor : ICurrentActor
    {
        private readonly ActorIdentity _actor;

        public NamedActor(ActorIdentity actor)
        {
            _actor = actor;
        }

        public string Id
        {
            get
            {
                return _actor.Id;
            }
        }

        public string Email
        {
            get
            {
                return _actor.Email;
            }
        }

        public string Name
        {
            get
            {
                return _actor.Name;
            }
        }
    }
}
