using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.ResendVerificationEmail;

[Collection(DatabaseCollection.Name)]
public sealed class ResendVerificationEmailTests : IAsyncLifetime
{
    private const string Route = "/auth/verificationEmails";

    private readonly DatabaseFixture _database;
    private readonly Guid _userId = Guid.NewGuid();

    public ResendVerificationEmailTests(DatabaseFixture database)
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
    public async Task ResendVerificationEmail_PendingAccount_IssuesANewLinkAndVoidsTheOldOnes()
    {
        // Arrange
        await SeedAccountAsync("PendingVerification");
        await SeedLinkAsync("interval '1 day'");

        // Act
        using HttpResponseMessage response = await PostAsync("Alice@Example.com");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("alice@example.com", body.RootElement.GetProperty("email").GetString());
        Assert.True(body.RootElement.TryGetProperty("linkExpiresAt", out _));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM authentication.email_verification_tokens WHERE voided_at IS NOT NULL"));
        Assert.Equal(1L, await CountAsync(
            "SELECT count(*) FROM authentication.email_verification_tokens WHERE voided_at IS NULL AND used_at IS NULL AND expires_at - issued_at = interval '24 hours'"));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM authentication.identity_events WHERE event_type = 'UserRegistered'"));
    }

    [Fact]
    public async Task ResendVerificationEmail_EmailWithNoAccount_IsRefusedAndSendsNothing()
    {
        // Act
        using HttpResponseMessage response = await PostAsync("nobody@example.com");

        // Assert
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "EMAIL_NOT_REGISTERED");
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM authentication.identity_events"));
    }

    [Fact]
    public async Task ResendVerificationEmail_AccountAlreadyActive_IsRefusedCarryingItsStatus()
    {
        // Arrange
        await SeedAccountAsync("Active");

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com");

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "EMAIL_ALREADY_VERIFIED");
        Assert.Equal("active", problem.RootElement.GetProperty("accountStatus").GetString());
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM authentication.identity_events"));
    }

    [Fact]
    public async Task ResendVerificationEmail_ResendAccepted30SecondsAgo_IsRateLimitedFor30Seconds()
    {
        // Arrange
        await SeedAccountAsync("PendingVerification");
        await SeedLinkAsync("interval '1 day'");
        await SeedLinkAsync("interval '30 seconds'");

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com");

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.InRange(problem.RootElement.GetProperty("retryAfterSeconds").GetInt32(), 29, 30);
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM authentication.email_verification_tokens"));
    }

    [Fact]
    public async Task ResendVerificationEmail_FiveResendsInTheLastDay_IsRateLimitedUntilTheOldestAgesOut()
    {
        // Arrange: the registration link does not count; the five resends do, the oldest 20 hours ago.
        await SeedAccountAsync("PendingVerification");
        await SeedLinkAsync("interval '2 days'");
        await SeedLinkAsync("interval '20 hours'");
        await SeedLinkAsync("interval '16 hours'");
        await SeedLinkAsync("interval '12 hours'");
        await SeedLinkAsync("interval '8 hours'");
        await SeedLinkAsync("interval '4 hours'");

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com");

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.InRange(problem.RootElement.GetProperty("retryAfterSeconds").GetInt32(), (4 * 3600) - 2, 4 * 3600);
    }

    [Fact]
    public async Task ResendVerificationEmail_MalformedEmail_IsRefusedAsValidationFailed()
    {
        // Act
        using HttpResponseMessage response = await PostAsync("not-an-email");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private async Task<HttpResponseMessage> PostAsync(string email)
    {
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        return await client.PostAsJsonAsync(Route, new { email });
    }

    private Task<long> CountAsync(string sql)
    {
        return _database.QueryScalarAsync<long>(sql);
    }

    private async Task SeedAccountAsync(string status)
    {
        await _database.SeedAsync(
            """
            INSERT INTO authentication.users (user_id, email, password_hash, full_name, role, status, failed_sign_in_count)
            VALUES (@UserId, 'alice@example.com', 'hash', 'Alice Rao', 'Customer', @Status, 0)
            """,
            new { UserId = _userId, Status = status });
    }

    private async Task SeedLinkAsync(string issuedAgo)
    {
        await _database.SeedAsync(
            $"""
            INSERT INTO authentication.email_verification_tokens (user_id, token_hash, issued_at, expires_at)
            VALUES (@UserId, @TokenHash, now() - {issuedAgo}, now() - {issuedAgo} + interval '24 hours')
            """,
            new { UserId = _userId, TokenHash = Guid.NewGuid().ToString() });
    }
}
