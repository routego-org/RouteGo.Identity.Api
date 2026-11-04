using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.OperatorOnboarding.ReapplyAsOperator;

[Collection(DatabaseCollection.Name)]
public sealed class ReapplyAsOperatorTests : IAsyncLifetime
{
    private const string Route = "/operators/reapplications";
    private const string BusCoEmail = "owner@busco.in";
    private const string Gstin = "27AAPFU0939F1ZV";
    private const string RejectReason = "GSTIN does not match company name";

    // The rejection's decision time is set relative to the database clock, the clock the wait is measured on.
    private const string InsertApplicationSql = """
        INSERT INTO operator_onboarding.operator_applications
            (operator_id, company_name, contact_email, gstin, status, submitted_at, reviewed_at, reviewed_by, reject_reason)
        VALUES (@OperatorId, 'BusCo Travels', 'bookings@busco.in', @Gstin, @Status,
                now() - @Age - interval '1 day',
                CASE WHEN @Status IN ('Approved', 'Rejected') THEN date_trunc('second', now() - @Age) END,
                CASE WHEN @Status IN ('Approved', 'Rejected') THEN @ReviewerId END,
                CASE WHEN @Status = 'Rejected' THEN @RejectReason END)
        """;

    private readonly DatabaseFixture _database;
    private Guid _adminId;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public ReapplyAsOperatorTests(DatabaseFixture database)
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
        _adminId = await TestSessions.SeedAccountAsync(_database, "lead@routego.in", "Admin", "Active");
        _factory = IdentityTestHost.For(_database);
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ReapplyAsOperator_RejectedEightDaysAgo_SubmitsANewApplicationForReview()
    {
        // Arrange
        Guid busCo = await SeedRejectedOperatorAsync(TimeSpan.FromDays(8));

        // Act
        using HttpResponseMessage response = await ReapplyAsync(TestSessions.Password, "BusCo Travels Pvt Ltd", Gstin);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pendingApproval", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("BusCo Travels Pvt Ltd", body.RootElement.GetProperty("companyName").GetString());
        Assert.Equal(busCo, body.RootElement.GetProperty("operatorId").GetGuid());
        JsonElement earlier = Assert.Single(body.RootElement.GetProperty("previousRejections").EnumerateArray());
        Assert.Equal(RejectReason, earlier.GetProperty("reason").GetString());
        Assert.Equal("PendingApproval", await QueryAsync<string>("SELECT status FROM authentication.users WHERE user_id = @Id", busCo));
        Assert.Equal(1L, await QueryAsync<long>(
            "SELECT count(*) FROM operator_onboarding.operator_applications WHERE status = 'Rejected' AND company_name = 'BusCo Travels'", busCo));
        Assert.Equal(0L, await QueryAsync<long>("SELECT count(*) FROM authentication.identity_events", busCo));
    }

    [Fact]
    public async Task ReapplyAsOperator_RejectedThreeDaysAgo_IsRefusedWithTheTimeItBecomesAllowed()
    {
        // Arrange
        Guid busCo = await SeedRejectedOperatorAsync(TimeSpan.FromDays(3));
        DateTime rejectedAt = (await QueryAsync<DateTime>(
            "SELECT reviewed_at FROM operator_onboarding.operator_applications WHERE operator_id = @Id", busCo))!;

        // Act
        using HttpResponseMessage response = await ReapplyAsync(TestSessions.Password, "BusCo Travels Pvt Ltd", Gstin);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.UnprocessableEntity, "REAPPLICATION_TOO_SOON");
        Assert.Equal(rejectedAt.AddDays(7), problem.RootElement.GetProperty("reapplyAllowedAt").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(1L, await QueryAsync<long>("SELECT count(*) FROM operator_onboarding.operator_applications", busCo));
    }

    [Fact]
    public async Task ReapplyAsOperator_WrongPassword_IsRefusedAndCountsTowardLockout()
    {
        // Arrange
        Guid busCo = await SeedRejectedOperatorAsync(TimeSpan.FromDays(8));

        // Act
        using HttpResponseMessage response = await ReapplyAsync("wrong-password-1", "BusCo Travels Pvt Ltd", Gstin);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(1, await QueryAsync<int>("SELECT failed_sign_in_count FROM authentication.users WHERE user_id = @Id", busCo));
    }

    [Fact]
    public async Task ReapplyAsOperator_EmailWithNoAccount_IsRefusedAsInvalidCredentials()
    {
        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, Reapplication("nobody@busco.in", TestSessions.Password, "BusCo", Gstin));

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task ReapplyAsOperator_LockedAccountWithTheRightPassword_IsRefusedCarryingLockedUntil()
    {
        // Arrange
        Guid busCo = await SeedRejectedOperatorAsync(TimeSpan.FromDays(8));
        await _database.SeedAsync(
            """
            UPDATE authentication.users
            SET failed_sign_in_count = 5, first_failed_sign_in_at = now() - interval '5 minutes', locked_until = now() + interval '10 minutes'
            WHERE user_id = @UserId
            """,
            new { UserId = busCo });

        // Act
        using HttpResponseMessage response = await ReapplyAsync(TestSessions.Password, "BusCo Travels Pvt Ltd", Gstin);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_LOCKED");
        Assert.True(problem.RootElement.TryGetProperty("lockedUntil", out _));
        Assert.Equal(1L, await QueryAsync<long>("SELECT count(*) FROM operator_onboarding.operator_applications", busCo));
    }

    [Theory]
    [InlineData("Customer", "Active", "active")]
    [InlineData("Operator", "PendingApproval", "pendingApproval")]
    [InlineData("Operator", "Active", "active")]
    public async Task ReapplyAsOperator_AccountNotARejectedOperator_IsRefusedCarryingItsStatus(string role, string status, string wireStatus)
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, BusCoEmail, role, status);

        // Act
        using HttpResponseMessage response = await ReapplyAsync(TestSessions.Password, "BusCo Travels Pvt Ltd", Gstin);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "OPERATOR_NOT_REJECTED");
        Assert.Equal(wireStatus, problem.RootElement.GetProperty("currentStatus").GetString());
    }

    [Fact]
    public async Task ReapplyAsOperator_GstinHeldByAnotherOperator_IsRefused()
    {
        // Arrange
        await SeedRejectedOperatorAsync(TimeSpan.FromDays(8));
        Guid other = await TestSessions.SeedAccountAsync(_database, "other@buses.in", "Operator", "Active");
        await SeedApplicationAsync(other, "Approved", TimeSpan.FromDays(2));

        // Act
        using HttpResponseMessage response = await ReapplyAsync(TestSessions.Password, "BusCo Travels Pvt Ltd", Gstin);

        // Assert
        using JsonDocument problem = await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "GSTIN_ALREADY_REGISTERED");
    }

    [Fact]
    public async Task ReapplyAsOperator_SameGstinAsItsOwnRejectedApplication_IsAccepted()
    {
        // Arrange
        await SeedRejectedOperatorAsync(TimeSpan.FromDays(8));

        // Act
        using HttpResponseMessage response = await ReapplyAsync(TestSessions.Password, "BusCo Travels", Gstin);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("27AAPFU0939F1Z", "BusCo Travels", "gstin")]
    [InlineData(Gstin, "B", "companyName")]
    public async Task ReapplyAsOperator_InvalidCompanyDetail_IsRefusedNamingTheField(string gstin, string companyName, string field)
    {
        // Arrange
        await SeedRejectedOperatorAsync(TimeSpan.FromDays(8));

        // Act
        using HttpResponseMessage response = await ReapplyAsync(TestSessions.Password, companyName, gstin);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    private static object Reapplication(string email, string password, string companyName, string gstin)
    {
        return new { email, password, companyName, contactEmail = "bookings@busco.in", gstin };
    }

    private static async Task<JsonDocument> AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private Task<HttpResponseMessage> ReapplyAsync(string password, string companyName, string gstin)
    {
        return Client.PostAsJsonAsync(Route, Reapplication(BusCoEmail, password, companyName, gstin));
    }

    private async Task<Guid> SeedRejectedOperatorAsync(TimeSpan rejectedAgo)
    {
        Guid busCo = await TestSessions.SeedAccountAsync(_database, BusCoEmail, "Operator", "Rejected");
        await SeedApplicationAsync(busCo, "Rejected", rejectedAgo);
        return busCo;
    }

    private async Task SeedApplicationAsync(Guid operatorId, string status, TimeSpan age)
    {
        await _database.SeedAsync(InsertApplicationSql, new
        {
            OperatorId = operatorId,
            Gstin,
            Status = status,
            Age = age,
            ReviewerId = _adminId,
            RejectReason,
        });
    }

    private Task<T?> QueryAsync<T>(string sql, Guid id)
    {
        return _database.QueryScalarAsync<T>(sql, new { Id = id });
    }
}
