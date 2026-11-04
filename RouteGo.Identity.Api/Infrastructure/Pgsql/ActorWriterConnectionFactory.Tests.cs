using Dapper;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Infrastructure.Pgsql;
using RouteGo.Identity.Api.Tests.Database;

namespace RouteGo.Identity.Api.Tests.Infrastructure.Pgsql;

[Collection(DatabaseCollection.Name)]
public sealed class ActorWriterConnectionFactoryTests
{
    private const string ReadStampSql = """
        SELECT current_setting('app.actor_id')    AS ActorId,
               current_setting('app.actor_email') AS ActorEmail,
               current_setting('app.actor_name')  AS ActorName,
               current_setting('app.action')      AS Action
        """;

    private readonly DatabaseFixture _database;

    public ActorWriterConnectionFactoryTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task OpenWriteTransactionAsync_NamedActor_StampsActorAndAction()
    {
        // Arrange
        var factory = new ActorWriterConnectionFactory(_database.ConnectionString, ConnectionRetryOptions.ManagedInstances);
        var actor = new ActorIdentity("3f6c1a2e-9b4d-4c1e-8f7a-2d5e6b7c8a90", "alice@example.com", "alice@example.com");

        // Act
        await using IWriterConnection writer = await factory.OpenWriteTransactionAsync(actor, "RegisterCustomer");
        StampRow stamp = await writer.Connection.QuerySingleAsync<StampRow>(ReadStampSql, transaction: writer.Transaction);

        // Assert
        Assert.Equal(actor.Id, stamp.ActorId);
        Assert.Equal(actor.Email, stamp.ActorEmail);
        Assert.Equal(actor.Name, stamp.ActorName);
        Assert.Equal("RegisterCustomer", stamp.Action);
    }

    [Fact]
    public async Task OpenWriteTransactionAsync_ActorIdOver64Characters_Throws()
    {
        // Arrange
        var factory = new ActorWriterConnectionFactory(_database.ConnectionString, ConnectionRetryOptions.ManagedInstances);
        var actor = new ActorIdentity(new string('a', 65), "alice@example.com", "alice@example.com");

        // Act
        Task<IWriterConnection> open = factory.OpenWriteTransactionAsync(actor, "RegisterCustomer");

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await open);
    }

    private sealed class StampRow
    {
        public string ActorId { get; init; } = string.Empty;

        public string ActorEmail { get; init; } = string.Empty;

        public string ActorName { get; init; } = string.Empty;

        public string Action { get; init; } = string.Empty;
    }
}
