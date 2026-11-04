using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.SuspendUser;

[Collection(DatabaseCollection.Name)]
public sealed class SuspendUserTests : IAsyncLifetime
{
    private const string AdminEmail = "lead@routego.in";
    private const string SpamEmail = "spam@example.com";
    private const string Reason = "Repeated fake bookings reported by operators";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _adminId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public SuspendUserTests(DatabaseFixture database)
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
    public async Task SuspendUser_ActiveCustomerOnTwoDevices_SuspendsEndsBothSessionsAndRecordsTheAudit()
    {
        // Arrange
        Guid spamId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Active");
        TestSession laptop = await TestSessions.SignInAsync(Client, SpamEmail);
        TestSession phone = await TestSessions.SignInAsync(Client, SpamEmail);

        // Act
        using HttpResponseMessage response = await SuspendAsync(AdminEmail, spamId, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("suspended", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Suspended", await GetStatusAsync(spamId));
        await AssertRefreshRefusedAsync(laptop.RefreshToken);
        await AssertRefreshRefusedAsync(phone.RefreshToken);
        Assert.Equal(Reason, await _database.QueryScalarAsync<string>(
            "SELECT suspend_reason FROM authentication.account_suspensions WHERE user_id = @UserId AND suspended_by = @AdminId",
            new { UserId = spamId, AdminId = _adminId }));
        using JsonDocument audit = await GetSingleAuditAsync();
        Assert.Equal("UserSuspended", audit.RootElement.GetProperty("action").GetString());
        Assert.Equal(_adminId.ToString(), audit.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(spamId.ToString(), audit.RootElement.GetProperty("entityId").GetString());
        Assert.Equal(Reason, audit.RootElement.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(1L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.identity_events"));
    }

    [Fact]
    public async Task SuspendUser_ActiveOperator_IsSuspendedAndCanNoLongerSignIn()
    {
        // Arrange
        Guid operatorId = await TestSessions.SeedAccountAsync(_database, "owner@busco.in", "Operator", "Active");

        // Act
        using HttpResponseMessage response = await SuspendAsync(AdminEmail, operatorId, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using HttpResponseMessage signIn = await Client.PostAsJsonAsync(
            "/auth/sessions", new { email = "owner@busco.in", password = TestSessions.Password });
        await AssertProblemCodeAsync(signIn, HttpStatusCode.Forbidden, "ACCOUNT_SUSPENDED");
    }

    [Fact]
    public async Task SuspendUser_ActiveAdmin_IsRefusedAndNothingChanges()
    {
        // Arrange
        Guid otherAdmin = await TestSessions.SeedAccountAsync(_database, "support2@routego.in", "Admin", "Active");

        // Act
        using HttpResponseMessage response = await SuspendAsync(AdminEmail, otherAdmin, new { reason = Reason });

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.UnprocessableEntity, "ADMIN_CANNOT_BE_SUSPENDED");
        Assert.Equal("Active", await GetStatusAsync(otherAdmin));
        Assert.Equal(0L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.identity_events"));
    }

    [Theory]
    [InlineData("Customer", "Suspended", "suspended")]
    [InlineData("Operator", "PendingApproval", "pendingApproval")]
    public async Task SuspendUser_AccountNotActive_IsRefusedCarryingItsStatus(string role, string status, string wireStatus)
    {
        // Arrange
        Guid userId = await TestSessions.SeedAccountAsync(_database, SpamEmail, role, status);

        // Act
        using HttpResponseMessage response = await SuspendAsync(AdminEmail, userId, new { reason = Reason });

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "USER_NOT_ACTIVE");
        Assert.Equal(wireStatus, problem.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal(0L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.account_suspensions"));
    }

    [Fact]
    public async Task SuspendUser_UnknownIdentifier_IsNotFound()
    {
        // Act
        using HttpResponseMessage response = await SuspendAsync(AdminEmail, Guid.NewGuid(), new { reason = Reason });

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory]
    [InlineData(9)]
    [InlineData(501)]
    public async Task SuspendUser_ReasonOutsideTenToFiveHundred_IsRefusedNamingReason(int length)
    {
        // Arrange
        Guid spamId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Active");

        // Act
        using HttpResponseMessage response = await SuspendAsync(AdminEmail, spamId, new { reason = new string('x', length) });

        // Assert
        await AssertReasonRefusedAsync(response);
        Assert.Equal("Active", await GetStatusAsync(spamId));
    }

    [Fact]
    public async Task SuspendUser_NoReason_IsRefusedNamingReason()
    {
        // Arrange
        Guid spamId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Active");

        // Act
        using HttpResponseMessage response = await SuspendAsync(AdminEmail, spamId, new { });

        // Assert
        await AssertReasonRefusedAsync(response);
    }

    [Fact]
    public async Task SuspendUser_SignedInCustomer_IsForbidden()
    {
        // Arrange
        Guid spamId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Active");

        // Act
        using HttpResponseMessage response = await SuspendAsync(SpamEmail, spamId, new { reason = Reason });

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal("Active", await GetStatusAsync(spamId));
    }

    [Fact]
    public async Task SuspendUser_WithoutAnAccessToken_IsUnauthorized()
    {
        // Arrange
        Guid spamId = await TestSessions.SeedAccountAsync(_database, SpamEmail, "Customer", "Active");

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(RouteFor(spamId), new { reason = Reason });

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    private static string RouteFor(Guid userId)
    {
        return $"/auth/admin/users/{userId}/suspend";
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

    private async Task AssertRefreshRefusedAsync(string refreshToken)
    {
        using HttpResponseMessage refresh = await Client.PostAsJsonAsync("/auth/sessions/refresh", new { refreshToken });
        using JsonDocument problem = await AssertProblemCodeAsync(refresh, HttpStatusCode.Unauthorized, "REFRESH_TOKEN_INVALID");
    }

    private async Task<HttpResponseMessage> SuspendAsync(string callerEmail, Guid userId, object body)
    {
        TestSession caller = await TestSessions.SignInAsync(Client, callerEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Post, RouteFor(userId), caller.AccessToken, body);
        return await Client.SendAsync(request);
    }

    private Task<string?> GetStatusAsync(Guid userId)
    {
        return _database.QueryScalarAsync<string>(
            "SELECT status FROM authentication.users WHERE user_id = @UserId", new { UserId = userId });
    }

    private async Task<JsonDocument> GetSingleAuditAsync()
    {
        string? data = await _database.QueryScalarAsync<string>(
            "SELECT data FROM authentication.identity_events WHERE event_type = 'AuditRecorded'");
        return JsonDocument.Parse(data!);
    }
}
