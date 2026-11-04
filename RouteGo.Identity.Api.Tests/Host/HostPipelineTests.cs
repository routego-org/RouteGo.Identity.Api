using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Behaviors;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Host;

[Collection(DatabaseCollection.Name)]
public sealed class HostPipelineTests : IAsyncLifetime
{
    private const string ActiveKeyId = "k-test";

    private readonly DatabaseFixture _database;

    public HostPipelineTests(DatabaseFixture database)
    {
        _database = database;
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
    public async Task Request_WithCorrelationIdHeader_EchoesTheSameId()
    {
        // Arrange
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "abc-123");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        Assert.Equal("abc-123", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task Request_WithoutCorrelationIdHeader_ReturnsAGeneratedUuid()
    {
        // Arrange
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/health");

        // Assert
        string correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.True(Guid.TryParse(correlationId, out _));
    }

    [Fact]
    public async Task Request_WithUnsafeCorrelationId_ReplacesIt()
    {
        // Arrange
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "bad id;drop");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        string correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.True(Guid.TryParse(correlationId, out _));
    }

    [Fact]
    public async Task Readiness_DatabaseReachableAndKeyActive_Returns200()
    {
        // Arrange
        var vault = new InMemorySigningKeyVault();
        vault.AddNewKey(ActiveKeyId);
        await SeedActiveKeyAsync();
        using ApiFactory factory = IdentityTestHost.For(_database, services => services.AddSingleton<ISigningKeyVault>(vault));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_NoActiveSigningKey_Returns503()
    {
        // Arrange
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_ActiveKeyMissingFromTheSecretStore_Returns503()
    {
        // Arrange
        await SeedActiveKeyAsync();
        var emptyVault = new InMemorySigningKeyVault();
        using ApiFactory factory = IdentityTestHost.For(_database, services => services.AddSingleton<ISigningKeyVault>(emptyVault));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_ActiveKeyStoredWithoutItsPrivateHalf_Returns503()
    {
        // Arrange
        await SeedActiveKeyAsync();
        var publicOnlyVault = new InMemorySigningKeyVault();
        using var rsa = RSA.Create(2048);
        publicOnlyVault.Put(ActiveKeyId, rsa.ExportSubjectPublicKeyInfoPem());
        using ApiFactory factory = IdentityTestHost.For(_database, services => services.AddSingleton<ISigningKeyVault>(publicOnlyVault));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private async Task SeedActiveKeyAsync()
    {
        await _database.SeedAsync(
            """
            INSERT INTO signing_keys.signing_keys (key_id, public_key, state, introduced_at, introduced_by)
            VALUES (@KeyId, 'public-key-pem', 'Active', now(), 'instructor')
            """,
            new { KeyId = ActiveKeyId });
    }
}
