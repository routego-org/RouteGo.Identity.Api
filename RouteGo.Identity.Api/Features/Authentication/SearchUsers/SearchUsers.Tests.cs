using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.SearchUsers;

[Collection(DatabaseCollection.Name)]
public sealed class SearchUsersTests : IAsyncLifetime
{
    private const string Route = "/auth/admin/users";
    private const string AdminEmail = "lead@routego.in";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private ApiFactory? _factory;
    private HttpClient? _client;

    public SearchUsersTests(DatabaseFixture database)
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
        await TestSessions.SeedAccountAsync(_database, AdminEmail, "Admin", "Active");
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
    public async Task SearchUsers_PartialEmailInAnyCase_ReturnsTheContainingAccounts()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, "alice@example.com", "Customer", "Active");
        await TestSessions.SeedAccountAsync(_database, "alicia@example.org", "Customer", "Active");
        await TestSessions.SeedAccountAsync(_database, "bob@example.com", "Customer", "Active");

        // Act
        using JsonDocument body = await SearchAsync("?email=ALIC");

        // Assert
        Assert.Equal(2, body.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement first = body.RootElement.GetProperty("items")[0];
        Assert.Equal("alicia@example.org", first.GetProperty("email").GetString());
        Assert.Equal(
            new[] { "createdAt", "email", "fullName", "phone", "role", "status", "userId" },
            first.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("customer", first.GetProperty("role").GetString());
        Assert.Equal("active", first.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SearchUsers_RoleAndStatusFilters_ReturnOnlyAccountsMatchingBoth()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, "active@example.com", "Customer", "Active");
        await TestSessions.SeedAccountAsync(_database, "suspended@example.com", "Customer", "Suspended");
        await TestSessions.SeedAccountAsync(_database, "operator@example.com", "Operator", "Active");

        // Act
        using JsonDocument body = await SearchAsync("?role=customer&status=suspended");

        // Assert
        Assert.Equal(1, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal("suspended@example.com", body.RootElement.GetProperty("items")[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task SearchUsers_SecondPageOfTwenty_ReturnsAccounts21To40NewestFirst()
    {
        // Arrange: the admin is the oldest of 45 accounts.
        await SeedNumberedCustomersAsync(44);

        // Act
        using JsonDocument body = await SearchAsync("?page=2&pageSize=20");

        // Assert
        Assert.Equal(45, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, body.RootElement.GetProperty("pageSize").GetInt32());
        JsonElement items = body.RootElement.GetProperty("items");
        Assert.Equal(20, items.GetArrayLength());
        Assert.Equal("user24@example.com", items[0].GetProperty("email").GetString());
        Assert.Equal("user05@example.com", items[19].GetProperty("email").GetString());
    }

    [Fact]
    public async Task SearchUsers_NoMatch_ReturnsAnEmptyPage()
    {
        // Act
        using JsonDocument body = await SearchAsync("?email=zzz-nobody");

        // Assert
        Assert.Equal(0, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task SearchUsers_SignedInCustomer_IsForbidden()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, "alice@example.com", "Customer", "Active");
        TestSession alice = await TestSessions.SignInAsync(Client, "alice@example.com");
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Get, Route, alice.AccessToken);

        // Act
        using HttpResponseMessage response = await Client.SendAsync(request);

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task SearchUsers_WithoutAnAccessToken_IsUnauthorized()
    {
        // Act
        using HttpResponseMessage response = await Client.GetAsync(Route);

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Theory]
    [InlineData("?status=deleted", "status")]
    [InlineData("?pageSize=500", "pageSize")]
    [InlineData("?sort=passwordHash", "sort")]
    public async Task SearchUsers_InvalidFilter_IsRefusedNamingIt(string query, string field)
    {
        // Arrange
        TestSession admin = await TestSessions.SignInAsync(Client, AdminEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Get, Route + query, admin.AccessToken);

        // Act
        using HttpResponseMessage response = await Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<JsonDocument> SearchAsync(string query)
    {
        TestSession admin = await TestSessions.SignInAsync(Client, AdminEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Get, Route + query, admin.AccessToken);
        using HttpResponseMessage response = await Client.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, content);
        return JsonDocument.Parse(content);
    }

    // One account per transaction: the creation time is the transaction's start time, and the
    // default order needs each account to be strictly newer than the one before.
    private async Task SeedNumberedCustomersAsync(int count)
    {
        for (int number = 1; number <= count; number++)
        {
            string email = $"user{number.ToString("00", CultureInfo.InvariantCulture)}@example.com";
            await TestSessions.SeedAccountAsync(_database, email, "Customer", "Active");
        }
    }
}
