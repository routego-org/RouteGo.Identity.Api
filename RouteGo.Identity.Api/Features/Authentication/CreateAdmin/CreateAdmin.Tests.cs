using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.CreateAdmin;

[Collection(DatabaseCollection.Name)]
public sealed class CreateAdminTests : IAsyncLifetime
{
    private const string Route = "/auth/admin/users";
    private const string AdminEmail = "lead@routego.in";
    private const string NewEmail = "support2@routego.in";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _adminId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public CreateAdminTests(DatabaseFixture database)
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
        _adminId = await TestSessions.SeedAccountAsync(_database, AdminEmail, "Admin", "Active");
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
    public async Task CreateAdmin_NewEmail_CreatesAnActiveVerifiedAdminAndRecordsOnlyTheAudit()
    {
        // Arrange
        var creation = new { email = NewEmail, password = "welcome2026a", fullName = "Support Two" };

        // Act
        using HttpResponseMessage response = await CreateAsAsync(AdminEmail, creation);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid newId = body.RootElement.GetProperty("userId").GetGuid();
        Assert.Equal("admin", body.RootElement.GetProperty("role").GetString());
        Assert.Equal("active", body.RootElement.GetProperty("status").GetString());
        Assert.True(await _database.QueryScalarAsync<bool>(
            "SELECT email_verified_at IS NOT NULL FROM authentication.users WHERE user_id = @UserId", new { UserId = newId }));
        Assert.Equal(0L, await CountEventsAsync("UserRegistered"));
        using JsonDocument audit = await GetSingleAuditAsync();
        Assert.Equal("AdminCreated", audit.RootElement.GetProperty("action").GetString());
        Assert.Equal(_adminId.ToString(), audit.RootElement.GetProperty("actorId").GetString());
        Assert.Equal("Admin", audit.RootElement.GetProperty("actorRole").GetString());
        Assert.Equal("Account", audit.RootElement.GetProperty("entityType").GetString());
        Assert.Equal(newId.ToString(), audit.RootElement.GetProperty("entityId").GetString());
        Assert.Equal(NewEmail, audit.RootElement.GetProperty("details").GetProperty("email").GetString());
        using HttpResponseMessage signIn = await Client.PostAsJsonAsync(
            "/auth/sessions", new { email = NewEmail, password = "welcome2026a" });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    }

    [Fact]
    public async Task CreateAdmin_EmailAlreadyACustomer_IsRefusedAndRecordsNothing()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, NewEmail, "Customer", "Active");
        var creation = new { email = "Support2@RouteGo.in", password = "welcome2026a", fullName = "Support Two" };

        // Act
        using HttpResponseMessage response = await CreateAsAsync(AdminEmail, creation);

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "EMAIL_ALREADY_REGISTERED");
        Assert.Equal(2L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.users"));
        Assert.Equal(0L, await CountEventsAsync("AuditRecorded"));
    }

    [Theory]
    [InlineData("short1", "Support Two", "password")]
    [InlineData("welcome2026a", "S", "fullName")]
    public async Task CreateAdmin_PasswordOrNameBreaksTheRules_IsRefusedNamingTheField(string password, string fullName, string field)
    {
        // Arrange
        var creation = new { email = NewEmail, password, fullName };

        // Act
        using HttpResponseMessage response = await CreateAsAsync(AdminEmail, creation);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task CreateAdmin_SignedInOperator_IsForbidden()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, "owner@busco.in", "Operator", "Active");
        var creation = new { email = NewEmail, password = "welcome2026a", fullName = "Support Two" };

        // Act
        using HttpResponseMessage response = await CreateAsAsync("owner@busco.in", creation);

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(0L, await CountEventsAsync("AuditRecorded"));
    }

    [Fact]
    public async Task CreateAdmin_WithoutAnAccessToken_IsUnauthorized()
    {
        // Arrange
        var creation = new { email = NewEmail, password = "welcome2026a", fullName = "Support Two" };

        // Act
        using HttpResponseMessage response = await Client.PostAsync(Route, JsonContent.Create(creation));

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<HttpResponseMessage> CreateAsAsync(string callerEmail, object creation)
    {
        TestSession caller = await TestSessions.SignInAsync(Client, callerEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Post, Route, caller.AccessToken, creation);
        return await Client.SendAsync(request);
    }

    private async Task<long> CountEventsAsync(string eventType)
    {
        return await _database.QueryScalarAsync<long>(
            "SELECT count(*) FROM authentication.identity_events WHERE event_type = @EventType", new { EventType = eventType });
    }

    private async Task<JsonDocument> GetSingleAuditAsync()
    {
        string? data = await _database.QueryScalarAsync<string>(
            "SELECT data FROM authentication.identity_events WHERE event_type = 'AuditRecorded'");
        return JsonDocument.Parse(data!);
    }
}
