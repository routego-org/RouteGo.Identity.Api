using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.OperatorOnboarding.ListOperatorApplications;

[Collection(DatabaseCollection.Name)]
public sealed class ListOperatorApplicationsTests : IAsyncLifetime
{
    private const string Route = "/operators/admin/applications";
    private const string AdminEmail = "lead@routego.in";
    private const string RejectReason = "GSTIN does not match company name";

    private const string InsertApplicationSql = """
        INSERT INTO operator_onboarding.operator_applications
            (operator_id, company_name, contact_email, gstin, status, submitted_at, reviewed_at, reviewed_by, reject_reason)
        VALUES (@OperatorId, @CompanyName, 'ops@example.in', @Gstin, @Status, @SubmittedAt,
                CASE WHEN @Status IN ('Approved', 'Rejected') THEN @SubmittedAt + interval '1 day' END,
                CASE WHEN @Status IN ('Approved', 'Rejected') THEN @ReviewerId END,
                CASE WHEN @Status = 'Rejected' THEN @RejectReason END)
        """;

    private static readonly DateTimeOffset _october1 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private Guid _adminId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public ListOperatorApplicationsTests(DatabaseFixture database)
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
    public async Task ListOperatorApplications_NoStatus_ReturnsPendingApprovalOldestFirstWithoutUnverified()
    {
        // Arrange
        await SeedOperatorWithApplicationAsync("first@buses.in", "10AAPFU0939F1ZV", "PendingApproval", _october1);
        await SeedOperatorWithApplicationAsync("third@buses.in", "11AAPFU0939F1ZV", "PendingApproval", _october1.AddDays(2));
        await SeedOperatorWithApplicationAsync("second@buses.in", "12AAPFU0939F1ZV", "PendingApproval", _october1.AddDays(1));
        await SeedOperatorWithApplicationAsync("unverified@buses.in", "13AAPFU0939F1ZV", "PendingVerification", _october1);

        // Act
        using JsonDocument body = await ListAsync(string.Empty);

        // Assert
        Assert.Equal(3, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            new[] { "first@buses.in", "second@buses.in", "third@buses.in" },
            body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("email").GetString()));
    }

    [Fact]
    public async Task ListOperatorApplications_OperatorThatReapplied_ShowsLatestWithTheEarlierRejection()
    {
        // Arrange
        Guid busCo = await SeedOperatorWithApplicationAsync("owner@busco.in", "27AAPFU0939F1ZV", "Rejected", _october1);
        await SeedApplicationAsync(busCo, "27AAPFU0939F1ZV", "PendingApproval", _october1.AddDays(9));

        // Act
        using JsonDocument body = await ListAsync(string.Empty);

        // Assert
        Assert.Equal(1, body.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement item = body.RootElement.GetProperty("items")[0];
        Assert.Equal("pendingApproval", item.GetProperty("status").GetString());
        JsonElement rejection = Assert.Single(item.GetProperty("previousRejections").EnumerateArray());
        Assert.Equal(_october1, rejection.GetProperty("submittedAt").GetDateTimeOffset());
        Assert.Equal(_october1.AddDays(1), rejection.GetProperty("rejectedAt").GetDateTimeOffset());
        Assert.Equal(RejectReason, rejection.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ListOperatorApplications_TwentyFiveRejectedSecondPage_ReturnsTheLastFiveOldestFirst()
    {
        // Arrange
        await SeedRejectedOperatorsAsync(25);

        // Act
        using JsonDocument body = await ListAsync("?status=rejected&page=2&pageSize=20");

        // Assert
        Assert.Equal(25, body.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement items = body.RootElement.GetProperty("items");
        Assert.Equal(5, items.GetArrayLength());
        Assert.Equal("operator21@buses.in", items[0].GetProperty("email").GetString());
        Assert.Equal("operator25@buses.in", items[4].GetProperty("email").GetString());
    }

    [Fact]
    public async Task ListOperatorApplications_NothingAwaitingReview_ReturnsAnEmptyPage()
    {
        // Act
        using JsonDocument body = await ListAsync(string.Empty);

        // Assert
        Assert.Equal(0, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ListOperatorApplications_UnknownStatus_IsRefusedNamingStatus()
    {
        // Arrange
        TestSession admin = await TestSessions.SignInAsync(Client, AdminEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Get, Route + "?status=archived", admin.AccessToken);

        // Act
        using HttpResponseMessage response = await Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("status", out _));
    }

    [Fact]
    public async Task ListOperatorApplications_SignedInOperator_IsForbidden()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, "owner@busco.in", "Operator", "Active");
        TestSession busCo = await TestSessions.SignInAsync(Client, "owner@busco.in");
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Get, Route, busCo.AccessToken);

        // Act
        using HttpResponseMessage response = await Client.SendAsync(request);

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task ListOperatorApplications_WithoutAnAccessToken_IsUnauthorized()
    {
        // Act
        using HttpResponseMessage response = await Client.GetAsync(Route);

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<JsonDocument> ListAsync(string query)
    {
        TestSession admin = await TestSessions.SignInAsync(Client, AdminEmail);
        using HttpRequestMessage request = TestSessions.Authorized(HttpMethod.Get, Route + query, admin.AccessToken);
        using HttpResponseMessage response = await Client.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, content);
        return JsonDocument.Parse(content);
    }

    private async Task<Guid> SeedOperatorWithApplicationAsync(string email, string gstin, string status, DateTimeOffset submittedAt)
    {
        Guid operatorId = await TestSessions.SeedAccountAsync(_database, email, "Operator", "PendingApproval");
        await SeedApplicationAsync(operatorId, gstin, status, submittedAt);
        return operatorId;
    }

    private async Task SeedApplicationAsync(Guid operatorId, string gstin, string status, DateTimeOffset submittedAt)
    {
        await _database.SeedAsync(InsertApplicationSql, new
        {
            OperatorId = operatorId,
            CompanyName = "Buses " + gstin,
            Gstin = gstin,
            Status = status,
            SubmittedAt = submittedAt,
            ReviewerId = _adminId,
            RejectReason,
        });
    }

    private async Task SeedRejectedOperatorsAsync(int count)
    {
        for (int number = 1; number <= count; number++)
        {
            string twoDigits = number.ToString("00", CultureInfo.InvariantCulture);
            await SeedOperatorWithApplicationAsync(
                $"operator{twoDigits}@buses.in", twoDigits + "AAPFU0939F1ZV", "Rejected", _october1.AddHours(number));
        }
    }
}
