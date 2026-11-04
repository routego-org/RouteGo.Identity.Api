using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.ReactivateUser;

[Collection(DatabaseCollection.Name)]
public sealed class ReactivateUserTests : IAsyncLifetime
{
    private const string AdminEmail = "lead@routego.in";
    private const string SpamEmail = "spam@example.com";
    private const string Reason = "Appeal reviewed and accepted";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _adminId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public ReactivateUserTests(DatabaseFixture database)
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
    public async Task ReactivateUser_SuspendedCustomer_IsActiveAgainAndSignsInWithoutTheOldSession()
    {
        // Arrange
        Guid spamId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Active");
        TestSession before = await TestSessions.SignInAsync(Client, SpamEmail);
        using HttpResponseMessage suspended = await PostAsAdminAsync(
            $"/auth/admin/users/{spamId}/suspend", new { reason = "Repeated fake bookings reported by operators" });

        // Act
        using HttpResponseMessage response = await PostAsAdminAsync(RouteFor(spamId), new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("active", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(Reason, await _database.QueryScalarAsync<string>(
            "SELECT reactivate_reason FROM authentication.account_suspensions WHERE user_id = @UserId AND reactivated_by = @AdminId",
            new { UserId = spamId, AdminId = _adminId }));
        using JsonDocument audit = await GetAuditAsync("UserReactivated");
        Assert.Equal(_adminId.ToString(), audit.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(spamId.ToString(), audit.RootElement.GetProperty("entityId").GetString());
        Assert.Equal(Reason, audit.RootElement.GetProperty("details").GetProperty("reason").GetString());
        await TestSessions.SignInAsync(Client, SpamEmail);
        using HttpResponseMessage oldSession = await Client.PostAsJsonAsync(
            "/auth/sessions/refresh", new { refreshToken = before.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
    }

    [Fact]
    public async Task ReactivateUser_ActiveAccount_IsRefusedCarryingStatusActive()
    {
        // Arrange
        Guid userId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Active");

        // Act
        using HttpResponseMessage response = await PostAsAdminAsync(RouteFor(userId), new { reason = Reason });

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "USER_NOT_SUSPENDED");
        Assert.Equal("active", problem.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal(0L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.identity_events"));
    }

    [Fact]
    public async Task ReactivateUser_UnknownIdentifier_IsNotFound()
    {
        // Act
        using HttpResponseMessage response = await PostAsAdminAsync(RouteFor(Guid.NewGuid()), new { reason = Reason });

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory]
    [InlineData(9)]
    [InlineData(501)]
    public async Task ReactivateUser_ReasonOutsideTenToFiveHundred_IsRefusedNamingReason(int length)
    {
        // Arrange
        Guid userId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Suspended");

        // Act
        using HttpResponseMessage response = await PostAsAdminAsync(RouteFor(userId), new { reason = new string('x', length) });

        // Assert
        await AssertReasonRefusedAsync(response);
    }

    [Fact]
    public async Task ReactivateUser_NoReason_IsRefusedNamingReason()
    {
        // Arrange
        Guid userId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Suspended");

        // Act
        using HttpResponseMessage response = await PostAsAdminAsync(RouteFor(userId), new { });

        // Assert
        await AssertReasonRefusedAsync(response);
    }

    [Fact]
    public async Task ReactivateUser_SignedInCustomer_IsForbidden()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, "alice@example.com", "Customer", "Active");
        Guid userId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Suspended");
        TestSession alice = await TestSessions.SignInAsync(Client, "alice@example.com");
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Post, RouteFor(userId), alice.AccessToken, new { reason = Reason });

        // Act
        using HttpResponseMessage response = await Client.SendAsync(request);

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task ReactivateUser_WithoutAnAccessToken_IsUnauthorized()
    {
        // Arrange
        Guid userId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Suspended");

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(RouteFor(userId), new { reason = Reason });

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    private static string RouteFor(Guid userId)
    {
        return $"/auth/admin/users/{userId}/reactivate";
    }

    private static async Task<JsonDocument> AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private static async Task AssertReasonRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("reason", out _));
    }

    private async Task<HttpResponseMessage> PostAsAdminAsync(string route, object body)
    {
        TestSession admin = await TestSessions.SignInAsync(Client, AdminEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Post, route, admin.AccessToken, body);
        return await Client.SendAsync(request);
    }

    private async Task<JsonDocument> GetAuditAsync(string action)
    {
        string? data = await _database.QueryScalarAsync<string>(
            "SELECT data FROM authentication.identity_events WHERE event_type = 'AuditRecorded' AND data::jsonb->>'action' = @Action",
            new { Action = action });
        return JsonDocument.Parse(data!);
    }
}
