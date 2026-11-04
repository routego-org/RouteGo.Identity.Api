using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.OperatorOnboarding.RejectOperator;

[Collection(DatabaseCollection.Name)]
public sealed class RejectOperatorTests : IAsyncLifetime
{
    private const string AdminEmail = "lead@routego.in";
    private const string BusCoEmail = "owner@busco.in";
    private const string Gstin = "27AAPFU0939F1ZV";
    private const string Reason = "GSTIN does not match company name";

    private const string InsertApplicationSql = """
        INSERT INTO operator_onboarding.operator_applications (operator_id, company_name, contact_email, gstin, status, submitted_at)
        VALUES (@OperatorId, 'BusCo Travels', 'bookings@busco.in', @Gstin, @Status, now())
        """;

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _adminId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public RejectOperatorTests(DatabaseFixture database)
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
    public async Task RejectOperator_PendingApplication_RejectsRecordsBothEventsAndFreesTheGstin()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");

        // Act
        using HttpResponseMessage response = await RejectAsAsync(AdminEmail, busCo, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(Reason, body.RootElement.GetProperty("rejectReason").GetString());
        Assert.Equal(_adminId, await _database.QueryScalarAsync<Guid>(
            "SELECT reviewed_by FROM operator_onboarding.operator_applications"));
        Assert.Equal("Rejected", await GetAccountStatusAsync(busCo));
        using JsonDocument rejected = await GetEventDataAsync("OperatorRejected");
        Assert.Equal(busCo, rejected.RootElement.GetProperty("operatorId").GetGuid());
        Assert.Equal(BusCoEmail, rejected.RootElement.GetProperty("email").GetString());
        Assert.Equal(Reason, rejected.RootElement.GetProperty("reason").GetString());
        using JsonDocument audit = await GetEventDataAsync("AuditRecorded");
        Assert.Equal("OperatorRejected", audit.RootElement.GetProperty("action").GetString());
        Assert.Equal(_adminId.ToString(), audit.RootElement.GetProperty("actorId").GetString());
        Assert.Equal("Operator", audit.RootElement.GetProperty("entityType").GetString());
        Assert.Equal(busCo.ToString(), audit.RootElement.GetProperty("entityId").GetString());
        Assert.Equal(Reason, audit.RootElement.GetProperty("details").GetProperty("reason").GetString());
        using HttpResponseMessage otherCompany = await Client.PostAsJsonAsync("/operators/applications", new
        {
            email = "owner@newbus.in",
            password = "travel2026x",
            fullName = "New Owner",
            companyName = "New Bus Co",
            contactEmail = "ops@newbus.in",
            gstin = Gstin,
        });
        Assert.Equal(HttpStatusCode.Created, otherCompany.StatusCode);
    }

    [Fact]
    public async Task RejectOperator_AlreadyApprovedByAnotherAdmin_IsRefusedAndChangesNothing()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");
        TestSession admin = await TestSessions.SignInAsync(Client, AdminEmail);
        using HttpRequestMessage approve = TestSessions.Authorized(
            HttpMethod.Post, $"/operators/admin/applications/{busCo}/approve", admin.AccessToken);
        using HttpResponseMessage approved = await Client.SendAsync(approve);

        // Act
        using HttpResponseMessage response = await RejectAsAsync(AdminEmail, busCo, new { reason = Reason });

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "APPLICATION_NOT_PENDING");
        Assert.Equal("approved", problem.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal("Active", await GetAccountStatusAsync(busCo));
        Assert.Equal(2L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.identity_events"));
    }

    [Fact]
    public async Task RejectOperator_EmailNotYetVerified_IsRefusedCarryingPendingVerification()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingVerification", "PendingVerification");

        // Act
        using HttpResponseMessage response = await RejectAsAsync(AdminEmail, busCo, new { reason = Reason });

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "APPLICATION_NOT_PENDING");
        Assert.Equal("pendingVerification", problem.RootElement.GetProperty("currentStatus").GetString());
    }

    [Fact]
    public async Task RejectOperator_UnknownIdentifier_IsNotFound()
    {
        // Act
        using HttpResponseMessage response = await RejectAsAsync(AdminEmail, Guid.NewGuid(), new { reason = Reason });

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory]
    [InlineData(9)]
    [InlineData(501)]
    public async Task RejectOperator_ReasonOutsideTenToFiveHundred_IsRefusedNamingReason(int length)
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");

        // Act
        using HttpResponseMessage response = await RejectAsAsync(AdminEmail, busCo, new { reason = new string('x', length) });

        // Assert
        await AssertReasonRefusedAsync(response);
        Assert.Equal("PendingApproval", await GetAccountStatusAsync(busCo));
    }

    [Fact]
    public async Task RejectOperator_NoReason_IsRefusedNamingReason()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");

        // Act
        using HttpResponseMessage response = await RejectAsAsync(AdminEmail, busCo, new { });

        // Assert
        await AssertReasonRefusedAsync(response);
    }

    [Fact]
    public async Task RejectOperator_SignedInOperator_IsForbidden()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");
        await TestSessions.SeedAccountAsync(_database, "other@buses.in", "Operator", "Active");

        // Act
        using HttpResponseMessage response = await RejectAsAsync("other@buses.in", busCo, new { reason = Reason });

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task RejectOperator_WithoutAnAccessToken_IsUnauthorized()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(RouteFor(busCo), new { reason = Reason });

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    private static string RouteFor(Guid operatorId)
    {
        return $"/operators/admin/applications/{operatorId}/reject";
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

    private async Task<HttpResponseMessage> RejectAsAsync(string callerEmail, Guid operatorId, object body)
    {
        TestSession caller = await TestSessions.SignInAsync(Client, callerEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Post, RouteFor(operatorId), caller.AccessToken, body);
        return await Client.SendAsync(request);
    }

    private async Task<Guid> SeedOperatorAsync(string accountStatus, string applicationStatus)
    {
        Guid operatorId = await TestSessions.SeedAccountAsync(_database, BusCoEmail, "Operator", accountStatus);
        await _database.SeedAsync(InsertApplicationSql, new { OperatorId = operatorId, Gstin, Status = applicationStatus });
        return operatorId;
    }

    private Task<string?> GetAccountStatusAsync(Guid userId)
    {
        return _database.QueryScalarAsync<string>(
            "SELECT status FROM authentication.users WHERE user_id = @UserId", new { UserId = userId });
    }

    private async Task<JsonDocument> GetEventDataAsync(string eventType)
    {
        string? data = await _database.QueryScalarAsync<string>(
            "SELECT data FROM authentication.identity_events WHERE event_type = @EventType", new { EventType = eventType });
        return JsonDocument.Parse(data!);
    }
}
