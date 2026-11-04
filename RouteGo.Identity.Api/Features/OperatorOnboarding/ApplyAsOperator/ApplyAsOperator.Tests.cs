using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.OperatorOnboarding.ApplyAsOperator;

[Collection(DatabaseCollection.Name)]
public sealed class ApplyAsOperatorTests : IAsyncLifetime
{
    private const string Route = "/operators/applications";
    private const string Email = "owner@busco.in";
    private const string Gstin = "27AAPFU0939F1ZV";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private ApiFactory? _factory;
    private HttpClient? _client;

    public ApplyAsOperatorTests(DatabaseFixture database)
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
    public async Task ApplyAsOperator_NewCompany_CreatesPendingAccountApplicationAndUserRegistered()
    {
        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, Application(Email, Gstin));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid operatorId = body.RootElement.GetProperty("operatorId").GetGuid();
        Assert.Equal("pendingVerification", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("BusCo Travels", body.RootElement.GetProperty("companyName").GetString());
        Assert.Equal("bookings@busco.in", body.RootElement.GetProperty("contactEmail").GetString());
        Assert.Equal(Gstin, body.RootElement.GetProperty("gstin").GetString());
        Assert.Equal(Email, body.RootElement.GetProperty("email").GetString());
        Assert.Equal("Ravi Kumar", body.RootElement.GetProperty("fullName").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("previousRejections").GetArrayLength());
        Assert.Equal("Operator", await QueryAsync<string>("SELECT role FROM authentication.users"));
        Assert.Equal("PendingVerification", await QueryAsync<string>("SELECT status FROM authentication.users"));
        using JsonDocument registered = await GetUserRegisteredAsync();
        Assert.Equal(operatorId, registered.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal("Operator", registered.RootElement.GetProperty("role").GetString());
        Assert.StartsWith("http://localhost:8080/auth/verify?token=", registered.RootElement.GetProperty("verificationLink").GetString());
    }

    [Fact]
    public async Task ApplyAsOperator_ThenVerified_AccountAndApplicationAwaitReviewAndAppearInTheQueue()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, "lead@routego.in", "Admin", "Active");
        using HttpResponseMessage applied = await Client.PostAsJsonAsync(Route, Application(Email, Gstin));
        string token = await GetVerificationTokenAsync();

        // Act
        using HttpResponseMessage verified = await Client.PostAsJsonAsync("/auth/emailVerifications", new { token });

        // Assert
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal("PendingApproval", await QueryAsync<string>("SELECT status FROM authentication.users WHERE role = 'Operator'"));
        Assert.Equal("PendingApproval", await QueryAsync<string>("SELECT status FROM operator_onboarding.operator_applications"));
        TestSession admin = await TestSessions.SignInAsync(Client, "lead@routego.in");
        using HttpRequestMessage list = TestSessions.Authorized(HttpMethod.Get, "/operators/admin/applications", admin.AccessToken);
        using HttpResponseMessage queue = await Client.SendAsync(list);
        using var body = JsonDocument.Parse(await queue.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(Gstin, body.RootElement.GetProperty("items")[0].GetProperty("gstin").GetString());
    }

    [Fact]
    public async Task ApplyAsOperator_EmailAlreadyACustomer_IsRefusedAndCreatesNothing()
    {
        // Arrange
        await TestSessions.SeedAccountAsync(_database, Email, "Customer", "Active");

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, Application("Owner@BusCo.in", Gstin));

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "EMAIL_ALREADY_REGISTERED");
        Assert.Equal(1L, await QueryAsync<long>("SELECT count(*) FROM authentication.users"));
        Assert.Equal(0L, await QueryAsync<long>("SELECT count(*) FROM operator_onboarding.operator_applications"));
        Assert.Equal(0L, await QueryAsync<long>("SELECT count(*) FROM authentication.identity_events"));
    }

    [Theory]
    [InlineData("PendingApproval")]
    [InlineData("Approved")]
    public async Task ApplyAsOperator_GstinHeldByAnotherOperator_IsRefusedAndCreatesNothing(string heldStatus)
    {
        // Arrange
        await SeedOtherOperatorApplicationAsync(heldStatus);

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, Application(Email, Gstin));

        // Assert
        await AssertProblemCodeAsync(response, HttpStatusCode.Conflict, "GSTIN_ALREADY_REGISTERED");
        Assert.Equal(1L, await QueryAsync<long>("SELECT count(*) FROM authentication.users"));
        Assert.Equal(0L, await QueryAsync<long>("SELECT count(*) FROM authentication.identity_events"));
    }

    [Fact]
    public async Task ApplyAsOperator_GstinOnlyOnARejectedApplication_IsAccepted()
    {
        // Arrange
        await SeedOtherOperatorApplicationAsync("Rejected");

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, Application(Email, Gstin));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("27AAPFU0939F1Z", "B", "bookings@busco.in", "gstin")]
    [InlineData(Gstin, "B", "bookings@busco.in", "companyName")]
    [InlineData(Gstin, "BusCo Travels", "bookings-at-busco", "contactEmail")]
    public async Task ApplyAsOperator_InvalidCompanyDetail_IsRefusedNamingTheField(
        string gstin, string companyName, string contactEmail, string field)
    {
        // Arrange
        var application = new
        {
            email = Email,
            password = "travel2026x",
            fullName = "Ravi Kumar",
            companyName,
            contactEmail,
            gstin,
        };

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, application);

        // Assert
        await AssertValidationFailedAsync(response, field);
    }

    [Fact]
    public async Task ApplyAsOperator_ShortPassword_IsRefusedNamingPassword()
    {
        // Arrange
        var application = new
        {
            email = Email,
            password = "short1",
            fullName = "Ravi Kumar",
            companyName = "BusCo Travels",
            contactEmail = "bookings@busco.in",
            gstin = Gstin,
        };

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, application);

        // Assert
        await AssertValidationFailedAsync(response, "password");
    }

    [Fact]
    public async Task ApplyAsOperator_WithoutFullName_IsRefusedNamingFullName()
    {
        // Arrange
        var application = new
        {
            email = Email,
            password = "travel2026x",
            companyName = "BusCo Travels",
            contactEmail = "bookings@busco.in",
            gstin = Gstin,
        };

        // Act
        using HttpResponseMessage response = await Client.PostAsJsonAsync(Route, application);

        // Assert
        await AssertValidationFailedAsync(response, "fullName");
    }

    private static object Application(string email, string gstin)
    {
        return new
        {
            email,
            password = "travel2026x",
            fullName = "Ravi Kumar",
            companyName = "BusCo Travels",
            contactEmail = "bookings@busco.in",
            gstin,
        };
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertValidationFailedAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    private Task<T?> QueryAsync<T>(string sql)
    {
        return _database.QueryScalarAsync<T>(sql);
    }

    private async Task<JsonDocument> GetUserRegisteredAsync()
    {
        string? data = await QueryAsync<string>(
            "SELECT data FROM authentication.identity_events WHERE event_type = 'UserRegistered'");
        return JsonDocument.Parse(data!);
    }

    private async Task<string> GetVerificationTokenAsync()
    {
        using JsonDocument registered = await GetUserRegisteredAsync();
        string link = registered.RootElement.GetProperty("verificationLink").GetString()!;
        return link.Substring(link.IndexOf("token=", StringComparison.Ordinal) + "token=".Length);
    }

    private async Task SeedOtherOperatorApplicationAsync(string status)
    {
        Guid otherOperator = await TestSessions.SeedAccountAsync(_database, "other@buses.in", "Operator", "Active");
        await _database.SeedAsync(
            """
            INSERT INTO operator_onboarding.operator_applications
                (operator_id, company_name, contact_email, gstin, status, submitted_at, reviewed_at, reviewed_by, reject_reason)
            VALUES (@OperatorId, 'Other Buses', 'ops@buses.in', @Gstin, @Status, now(),
                    CASE WHEN @Status IN ('Approved', 'Rejected') THEN now() END,
                    CASE WHEN @Status IN ('Approved', 'Rejected') THEN @OperatorId END,
                    CASE WHEN @Status = 'Rejected' THEN 'GSTIN does not match company name' END)
            """,
            new { OperatorId = otherOperator, Gstin, Status = status });
    }
}
