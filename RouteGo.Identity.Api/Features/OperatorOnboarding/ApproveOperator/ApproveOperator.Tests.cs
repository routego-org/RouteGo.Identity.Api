using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.OperatorOnboarding.ApproveOperator;

[Collection(DatabaseCollection.Name)]
public sealed class ApproveOperatorTests : IAsyncLifetime
{
    private const string AdminEmail = "lead@routego.in";
    private const string BusCoEmail = "owner@busco.in";
    private const string Gstin = "27AAPFU0939F1ZV";

    private const string InsertApplicationSql = """
        INSERT INTO operator_onboarding.operator_applications (operator_id, company_name, contact_email, gstin, status, submitted_at)
        VALUES (@OperatorId, 'BusCo Travels', 'bookings@busco.in', @Gstin, @Status, now())
        """;

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _adminId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public ApproveOperatorTests(DatabaseFixture database)
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
    public async Task ApproveOperator_PendingApplication_ApprovesActivatesAndRecordsBothEvents()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");

        // Act
        using HttpResponseMessage response = await ApproveAsAsync(AdminEmail, busCo);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("approved", body.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("reviewedAt").ValueKind);
        Assert.Equal(_adminId, await _database.QueryScalarAsync<Guid>(
            "SELECT reviewed_by FROM operator_onboarding.operator_applications"));
        Assert.Equal("Active", await GetAccountStatusAsync(busCo));
        using JsonDocument approved = await GetEventDataAsync("OperatorApproved");
        Assert.Equal(busCo, approved.RootElement.GetProperty("operatorId").GetGuid());
        Assert.Equal("BusCo Travels", approved.RootElement.GetProperty("companyName").GetString());
        Assert.Equal(BusCoEmail, approved.RootElement.GetProperty("email").GetString());
        using JsonDocument audit = await GetEventDataAsync("AuditRecorded");
        Assert.Equal("OperatorApproved", audit.RootElement.GetProperty("action").GetString());
        Assert.Equal(_adminId.ToString(), audit.RootElement.GetProperty("actorId").GetString());
        Assert.Equal("Operator", audit.RootElement.GetProperty("entityType").GetString());
        Assert.Equal(busCo.ToString(), audit.RootElement.GetProperty("entityId").GetString());
        Assert.Equal("BusCo Travels", audit.RootElement.GetProperty("details").GetProperty("companyName").GetString());
        Assert.Equal(Gstin, audit.RootElement.GetProperty("details").GetProperty("gstin").GetString());
        TestSession signedIn = await TestSessions.SignInAsync(Client, BusCoEmail);
        var token = new JsonWebToken(signedIn.AccessToken);
        Assert.Equal(busCo.ToString(), token.GetClaim("operatorId").Value);
    }

    [Fact]
    public async Task ApproveOperator_AlreadyApprovedBySecondAdmin_IsRefusedAndRecordsNothingMore()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");
        using HttpResponseMessage first = await ApproveAsAsync(AdminEmail, busCo);

        // Act
        using HttpResponseMessage second = await ApproveAsAsync(AdminEmail, busCo);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(second, HttpStatusCode.Conflict, "APPLICATION_NOT_PENDING");
        Assert.Equal("approved", problem.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal(2L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.identity_events"));
    }

    [Fact]
    public async Task ApproveOperator_EmailNotYetVerified_IsRefusedCarryingPendingVerification()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingVerification", "PendingVerification");

        // Act
        using HttpResponseMessage response = await ApproveAsAsync(AdminEmail, busCo);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "APPLICATION_NOT_PENDING");
        Assert.Equal("pendingVerification", problem.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal("PendingVerification", await GetAccountStatusAsync(busCo));
    }

    [Fact]
    public async Task ApproveOperator_IdentifierThatIsNotAnOperator_IsNotFound()
    {
        // Arrange
        Guid customer = await TestSessions.SeedAccountAsync(_database, "alice@example.com", "Customer", "Active");

        // Act
        using HttpResponseMessage unknown = await ApproveAsAsync(AdminEmail, Guid.NewGuid());
        using HttpResponseMessage notOperator = await ApproveAsAsync(AdminEmail, customer);

        // Assert
        using JsonDocument unknownProblem = await AssertProblemCodeAsync(unknown, HttpStatusCode.NotFound, "NOT_FOUND");
        using JsonDocument customerProblem = await AssertProblemCodeAsync(notOperator, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task ApproveOperator_SignedInOperator_IsForbidden()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");
        await TestSessions.SeedAccountAsync(_database, "other@buses.in", "Operator", "Active");

        // Act
        using HttpResponseMessage response = await ApproveAsAsync("other@buses.in", busCo);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal("PendingApproval", await GetAccountStatusAsync(busCo));
    }

    [Fact]
    public async Task ApproveOperator_WithoutAnAccessToken_IsUnauthorized()
    {
        // Arrange
        Guid busCo = await SeedOperatorAsync("PendingApproval", "PendingApproval");

        // Act
        using HttpResponseMessage response = await Client.PostAsync(RouteFor(busCo), null);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    private static string RouteFor(Guid operatorId)
    {
        return $"/operators/admin/applications/{operatorId}/approve";
    }

    private static async Task<JsonDocument> AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private async Task<HttpResponseMessage> ApproveAsAsync(string callerEmail, Guid operatorId)
    {
        TestSession caller = await TestSessions.SignInAsync(Client, callerEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Post, RouteFor(operatorId), caller.AccessToken);
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
