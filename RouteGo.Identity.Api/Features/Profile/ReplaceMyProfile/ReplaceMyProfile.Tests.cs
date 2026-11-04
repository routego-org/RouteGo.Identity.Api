using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Profile.ReplaceMyProfile;

[Collection(DatabaseCollection.Name)]
public sealed class ReplaceMyProfileTests : IAsyncLifetime
{
    private const string Route = "/me";
    private const string Email = "alice@example.com";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _userId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public ReplaceMyProfileTests(DatabaseFixture database)
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
    public async Task ReplaceMyProfile_NewNameAndPhone_StoresAndReturnsTheUpdatedProfile()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(
            alice.AccessToken, """{"fullName":"Alice R. Rao","phone":"+918765432109"}""");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(_userId, body.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal("Alice R. Rao", body.RootElement.GetProperty("fullName").GetString());
        Assert.Equal("+918765432109", body.RootElement.GetProperty("phone").GetString());
        Assert.Equal("customer", body.RootElement.GetProperty("role").GetString());
        Assert.Equal("Alice R. Rao", await GetColumnAsync("full_name"));
        Assert.Equal("+918765432109", await GetColumnAsync("phone"));
    }

    [Fact]
    public async Task ReplaceMyProfile_PhoneNull_ClearsThePhone()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(alice.AccessToken, """{"fullName":"Alice Rao","phone":null}""");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await GetColumnAsync("phone"));
    }

    [Fact]
    public async Task ReplaceMyProfile_PhoneWithoutPrefix_IsKeptAndReturnedWithPlus91()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(alice.AccessToken, """{"fullName":"Alice Rao","phone":"9876543210"}""");

        // Assert
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("+919876543210", body.RootElement.GetProperty("phone").GetString());
        Assert.Equal("+919876543210", await GetColumnAsync("phone"));
    }

    [Theory]
    [InlineData("""{"fullName":"Alice R. Rao"}""", "phone")]
    [InlineData("""{"fullName":"A","phone":null}""", "fullName")]
    [InlineData("""{"fullName":"Alice R. Rao","phone":"12345"}""", "phone")]
    public async Task ReplaceMyProfile_InvalidReplacement_IsRefusedNamingTheFieldAndChangesNothing(string json, string field)
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(alice.AccessToken, json);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal("Alice Rao", await GetColumnAsync("full_name"));
        Assert.Equal("+919876543210", await GetColumnAsync("phone"));
    }

    [Fact]
    public async Task ReplaceMyProfile_WithEmailAndRole_IgnoresThem()
    {
        // Arrange
        TestSession alice = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(
            alice.AccessToken,
            """{"fullName":"Alice Rao","phone":null,"email":"mallory@example.com","role":"admin","status":"suspended"}""");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Email, await GetColumnAsync("email"));
        Assert.Equal("Customer", await GetColumnAsync("role"));
        Assert.Equal("Active", await GetColumnAsync("status"));
    }

    [Fact]
    public async Task ReplaceMyProfile_WithoutAnAccessToken_IsUnauthorized()
    {
        // Arrange
        using var content = new StringContent("""{"fullName":"Alice Rao","phone":null}""", Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await Client.PutAsync(Route, content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNAUTHORIZED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Alice Rao", await GetColumnAsync("full_name"));
    }

    private async Task<HttpResponseMessage> ReplaceAsync(string accessToken, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Client.SendAsync(request);
    }

    private Task<string?> GetColumnAsync(string column)
    {
        return _database.QueryScalarAsync<string?>(
            $"SELECT {column} FROM authentication.users WHERE user_id = @UserId", new { UserId = _userId });
    }
}
