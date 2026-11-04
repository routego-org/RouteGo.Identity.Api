using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.RegisterCustomer;

[Collection(DatabaseCollection.Name)]
public sealed class RegisterCustomerTests : IAsyncLifetime
{
    private const string Route = "/auth/registrations";

    private readonly DatabaseFixture _database;

    public RegisterCustomerTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RegisterCustomer_NewEmail_CreatesPendingCustomerAndRecordsUserRegistered()
    {
        // Arrange
        var registration = new { email = "alice@example.com", password = "travel2026x", fullName = "Alice Rao", phone = "9876543210" };

        // Act
        using HttpResponseMessage response = await PostAsync(registration);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid userId = body.RootElement.GetProperty("userId").GetGuid();
        Assert.Equal("customer", body.RootElement.GetProperty("role").GetString());
        Assert.Equal("pendingVerification", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("+919876543210", body.RootElement.GetProperty("phone").GetString());
        Assert.Equal($"/auth/admin/users/{userId}", response.Headers.Location!.OriginalString);
        Assert.Equal("PendingVerification", await QueryUserAsync<string>("status"));
        using JsonDocument data = await GetUserRegisteredDataAsync();
        Assert.Equal(userId, data.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal("alice@example.com", data.RootElement.GetProperty("email").GetString());
        Assert.Equal("Alice Rao", data.RootElement.GetProperty("name").GetString());
        Assert.Equal("Customer", data.RootElement.GetProperty("role").GetString());
        Assert.StartsWith("http://localhost:8080/auth/verify?token=", data.RootElement.GetProperty("verificationLink").GetString());
        Assert.Equal(userId.ToString(), await QueryUserAsync<string>("created_by_id"));
    }

    [Fact]
    public async Task RegisterCustomer_WithoutPhone_CreatesTheAccountWithNoPhone()
    {
        // Arrange
        var registration = new { email = "ravi@example.com", password = "travel2026x", fullName = "Ravi Kumar" };

        // Act
        using HttpResponseMessage response = await PostAsync(registration);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(await QueryUserAsync<string>("phone"));
    }

    [Fact]
    public async Task RegisterCustomer_EmailRegisteredInAnyRoleAndCase_IsRefusedAndCreatesNothing()
    {
        // Arrange
        await SeedOperatorAsync("Owner@BusCo.in");
        var registration = new { email = "owner@busco.in", password = "travel2026x", fullName = "Bus Owner" };

        // Act
        using HttpResponseMessage response = await PostAsync(registration);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("EMAIL_ALREADY_REGISTERED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("urn:routego:identity:problems:email-already-registered", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(1L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.users"));
        Assert.Equal(0L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.identity_events"));
    }

    [Theory]
    [InlineData("short1")]
    [InlineData("onlyletterspassword")]
    [InlineData("1234567890")]
    public async Task RegisterCustomer_PasswordBreaksThePolicy_IsRefusedNamingPassword(string password)
    {
        // Arrange
        var registration = new { email = "alice@example.com", password, fullName = "Alice Rao" };

        // Act
        using HttpResponseMessage response = await PostAsync(registration);

        // Assert
        await AssertValidationFailedAsync(response, "password");
        Assert.Equal(0L, await _database.QueryScalarAsync<long>("SELECT count(*) FROM authentication.users"));
    }

    [Theory]
    [InlineData("A", null, "fullName")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", null, "fullName")]
    [InlineData("Alice Rao", "5876543210", "phone")]
    [InlineData("Alice Rao", "98765", "phone")]
    [InlineData("Alice Rao", "9876543210\n", "phone")]
    [InlineData("😀", null, "fullName")]
    public async Task RegisterCustomer_InvalidNameOrPhone_IsRefusedNamingTheField(string fullName, string? phone, string field)
    {
        // Arrange
        var registration = new { email = "alice@example.com", password = "travel2026x", fullName, phone };

        // Act
        using HttpResponseMessage response = await PostAsync(registration);

        // Assert
        await AssertValidationFailedAsync(response, field);
    }

    [Fact]
    public async Task RegisterCustomer_MessagingUnavailable_SucceedsAndKeepsTheEventForLaterDelivery()
    {
        // Arrange: the test host runs no publisher, exactly as if messaging were down.
        var registration = new { email = "alice@example.com", password = "travel2026x", fullName = "Alice Rao" };

        // Act
        using HttpResponseMessage response = await PostAsync(registration);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1L, await _database.QueryScalarAsync<long>(
            "SELECT count(*) FROM authentication.identity_events WHERE event_type = 'UserRegistered' AND delivered_at IS NULL"));
    }

    [Fact]
    public async Task RegisterCustomer_Success_IssuesASingleUseLinkValidFor24HoursAndKeepsOnlyItsHash()
    {
        // Arrange
        var registration = new { email = "alice@example.com", password = "travel2026x", fullName = "Alice Rao" };

        // Act
        using HttpResponseMessage response = await PostAsync(registration);

        // Assert
        using JsonDocument data = await GetUserRegisteredDataAsync();
        string link = data.RootElement.GetProperty("verificationLink").GetString()!;
        string token = link.Substring(link.IndexOf("token=", StringComparison.Ordinal) + "token=".Length);
        string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        Assert.Equal(expectedHash, await _database.QueryScalarAsync<string>(
            "SELECT token_hash FROM authentication.email_verification_tokens"));
        Assert.Equal(TimeSpan.FromHours(24), await _database.QueryScalarAsync<TimeSpan>(
            "SELECT expires_at - issued_at FROM authentication.email_verification_tokens"));
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(
            "SELECT count(*) FROM authentication.email_verification_tokens WHERE token_hash = @Token", new { Token = token }));
    }

    private static async Task AssertValidationFailedAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    private async Task<HttpResponseMessage> PostAsync(object registration)
    {
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        return await client.PostAsJsonAsync(Route, registration);
    }

    private Task<T?> QueryUserAsync<T>(string column)
    {
        return _database.QueryScalarAsync<T>($"SELECT {column} FROM authentication.users");
    }

    private async Task<JsonDocument> GetUserRegisteredDataAsync()
    {
        string? data = await _database.QueryScalarAsync<string>(
            "SELECT data FROM authentication.identity_events WHERE event_type = 'UserRegistered'");
        return JsonDocument.Parse(data!);
    }

    private async Task SeedOperatorAsync(string email)
    {
        await _database.SeedAsync(
            """
            INSERT INTO authentication.users (email, password_hash, full_name, role, status, failed_sign_in_count)
            VALUES (@Email, 'hash', 'Bus Owner', 'Operator', 'PendingApproval', 0)
            """,
            new { Email = email });
    }
}
