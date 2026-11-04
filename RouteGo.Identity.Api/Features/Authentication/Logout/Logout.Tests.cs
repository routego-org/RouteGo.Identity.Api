using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.Logout;

[Collection(DatabaseCollection.Name)]
public sealed class LogoutTests : IAsyncLifetime
{
    private const string Route = "/auth/sessions/revoke";
    private const string Email = "alice@example.com";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _userId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public LogoutTests(DatabaseFixture database)
    {
        _database = database;
    }

    private HttpClient Client
    {
        get
        {
            return _client!;
        }
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        await TestSigningKeys.SeedActiveAsync(_database, _vault);
        _userId = await TestSessions.SeedAccountAsync(_database, Email, "Customer", "Active");
        await TestSessions.SeedAccountAsync(_database, "bob@example.com", "Customer", "Active");
        _factory = IdentityTestHost.For(_database, services => services.AddSingleton<ISigningKeyVault>(_vault));
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Logout_ThisDevice_EndsItsSessionAndLeavesOtherDevicesSignedIn()
    {
        // Arrange
        TestSession laptop = await TestSessions.SignInAsync(Client, Email);
        TestSession phone = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await LogoutAsync(laptop.AccessToken, laptop.RefreshToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using HttpResponseMessage laptopRefresh = await RefreshAsync(laptop.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, laptopRefresh.StatusCode);
        using HttpResponseMessage phoneRefresh = await RefreshAsync(phone.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, phoneRefresh.StatusCode);
    }

    [Fact]
    public async Task Logout_RepeatedOrWithSomeoneElsesToken_IsHarmless()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);
        TestSession bob = await TestSessions.SignInAsync(Client, "bob@example.com");
        using HttpResponseMessage first = await LogoutAsync(alice.AccessToken, alice.RefreshToken);

        // Act
        using HttpResponseMessage again = await LogoutAsync(alice.AccessToken, alice.RefreshToken);
        using HttpResponseMessage notHers = await LogoutAsync(alice.AccessToken, bob.RefreshToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, notHers.StatusCode);
        using HttpResponseMessage bobRefresh = await RefreshAsync(bob.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, bobRefresh.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutAnAccessToken_IsUnauthorized()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, new { refreshToken = alice.RefreshToken });

        // Assert
        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Logout_WithAnExpiredAccessToken_IsUnauthorized()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);
        string expired = await TestSessions.CreateExpiredAccessTokenAsync(_vault, _userId);

        // Act
        using HttpResponseMessage response = await LogoutAsync(expired, alice.RefreshToken);

        // Assert
        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Logout_EmptyRefreshToken_IsRefusedAsValidationFailed()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await LogoutAsync(alice.AccessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNAUTHORIZED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("urn:routego:identity:problems:unauthorized", problem.RootElement.GetProperty("type").GetString());
    }

    private async Task<HttpResponseMessage> LogoutAsync(string accessToken, string refreshToken)
    {
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Post, Route, accessToken, new { refreshToken });
        return await Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken)
    {
        return Client.PostAsJsonAsync("/auth/sessions/refresh", new { refreshToken });
    }
}
