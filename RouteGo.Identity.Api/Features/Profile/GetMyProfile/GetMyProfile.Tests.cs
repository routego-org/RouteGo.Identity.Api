using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Profile.GetMyProfile;

[Collection(DatabaseCollection.Name)]
public sealed class GetMyProfileTests : IAsyncLifetime
{
    private const string Route = "/me";
    private const string Email = "alice@example.com";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _userId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public GetMyProfileTests(DatabaseFixture database)
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
        await _database.SeedAsync(
            "UPDATE authentication.users SET full_name = 'Alice Rao', phone = '+919876543210' WHERE user_id = @UserId",
            new { UserId = _userId });
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
    public async Task GetMyProfile_SignedInCustomer_ReturnsTheirOwnProfile()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Get, Route, alice.AccessToken);

        // Act
        using HttpResponseMessage response = await Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(_userId, body.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal(Email, body.RootElement.GetProperty("email").GetString());
        Assert.Equal("Alice Rao", body.RootElement.GetProperty("fullName").GetString());
        Assert.Equal("+919876543210", body.RootElement.GetProperty("phone").GetString());
        Assert.Equal("customer", body.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task GetMyProfile_WithoutAnAccessToken_IsUnauthorized()
    {
        // Act
        using HttpResponseMessage response = await Client.GetAsync(Route);

        // Assert
        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task GetMyProfile_WithAnExpiredAccessToken_IsUnauthorized()
    {
        // Arrange
        string expired = await TestSessions.CreateExpiredAccessTokenAsync(_vault, _userId);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Get, Route, expired);

        // Act
        using HttpResponseMessage response = await Client.SendAsync(request);

        // Assert
        await AssertUnauthorizedAsync(response);
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNAUTHORIZED", problem.RootElement.GetProperty("code").GetString());
    }
}
