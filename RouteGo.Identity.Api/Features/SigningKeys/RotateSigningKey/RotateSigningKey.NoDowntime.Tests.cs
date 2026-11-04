using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Features.SigningKeys.RotateSigningKey;
using RouteGo.Identity.Api.Infrastructure.Pgsql;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.SigningKeys.RotateSigningKey;

[Collection(DatabaseCollection.Name)]
public sealed class RotateSigningKeyNoDowntimeTests : IAsyncLifetime
{
    private const string NewKey = "k-next";
    private const string Email = "alice@example.com";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private readonly FixedClock _rotationClock = new FixedClock(new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero));

    public RotateSigningKeyNoDowntimeTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        await TestSigningKeys.SeedActiveAsync(_database, _vault);
        await TestSessions.SeedAccountAsync(_database, Email, "Customer", "Active");
        _vault.AddNewKey(NewKey);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FullRotation_SignedInBeforeActivate_TokensKeepValidatingAndTheSessionKeepsRefreshing()
    {
        // Arrange
        using ApiFactory factory = IdentityTestHost.For(_database, services => services.AddSingleton<ISigningKeyVault>(_vault));
        using HttpClient client = factory.CreateClient();
        RotateSigningKeyHandler rotation = CreateRotationHandler();
        TestSession before = await TestSessions.SignInAsync(client, Email);
        await RunStageAsync(rotation, "introduce", NewKey, TimeSpan.Zero);

        // Act
        await RunStageAsync(rotation, "activate", NewKey, TimeSpan.FromMinutes(11));
        HttpStatusCode oldTokenAfterActivate = await ProbeAsync(client, before.AccessToken);
        TestSession refreshed = await RefreshAsync(client, before.RefreshToken);
        await RunStageAsync(rotation, "retire", TestSigningKeys.ActiveKeyId, TimeSpan.FromMinutes(27));
        TestSession afterRetire = await RefreshAsync(client, refreshed.RefreshToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, oldTokenAfterActivate);
        Assert.Equal(NewKey, new JsonWebToken(refreshed.AccessToken).Kid);
        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync(client, refreshed.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, await ProbeAsync(client, afterRetire.AccessToken));
    }

    private static async Task<HttpStatusCode> ProbeAsync(HttpClient client, string accessToken)
    {
        // Signing out an unknown session changes nothing and answers 204 whenever the token is accepted.
        using HttpRequestMessage request = TestSessions.Authorized(
            HttpMethod.Post, "/auth/sessions/revoke", accessToken, new { refreshToken = "not-a-session" });
        using HttpResponseMessage response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<TestSession> RefreshAsync(HttpClient client, string refreshToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/sessions/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new TestSession(
            body.RootElement.GetProperty("accessToken").GetString()!,
            body.RootElement.GetProperty("refreshToken").GetString()!);
    }

    private RotateSigningKeyHandler CreateRotationHandler()
    {
        var writerFactory = new ActorWriterConnectionFactory(_database.ConnectionString, ConnectionRetryOptions.ManagedInstances);
        var settings = new SigningKeyRotationSettings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));
        return new RotateSigningKeyHandler(writerFactory, _vault, _rotationClock, settings);
    }

    private async Task RunStageAsync(RotateSigningKeyHandler rotation, string stage, string keyId, TimeSpan sinceStart)
    {
        _rotationClock.UtcNow = new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero) + sinceStart;
        RotateSigningKeyResult result = await rotation.HandleAsync(
            new RotateSigningKeyRequest(stage, keyId, "instructor"), "test-correlation", CancellationToken.None);
        Assert.Equal(RotateSigningKeyOutcome.Completed, result.Outcome);
    }
}
