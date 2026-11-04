using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.Login;

[Collection(DatabaseCollection.Name)]
public sealed class LoginTests : IAsyncLifetime
{
    private const string Route = "/auth/sessions";
    private const string Password = "travel2026x";

    private static readonly string _passwordHash = PasswordHashing.Hash(Password);

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private readonly CapturingLoggerProvider _logs = new CapturingLoggerProvider();
    private readonly Guid _userId = Guid.NewGuid();

    public LoginTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        await TestSigningKeys.SeedActiveAsync(_database, _vault);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Login_ActiveCustomer_ReturnsTokensAndResetsFailures()
    {
        // Arrange
        await SeedAccountAsync("Customer", "Active");
        await SetLockoutAsync(2, "now() - interval '1 minute'", "NULL");

        // Act
        using HttpResponseMessage response = await PostAsync("ALICE@example.com", Password);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var accessToken = new JsonWebToken(body.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal(_userId.ToString(), accessToken.Subject);
        Assert.Equal("alice@example.com", accessToken.GetClaim("email").Value);
        Assert.Equal("Customer", accessToken.GetClaim("role").Value);
        Assert.Equal(TimeSpan.FromMinutes(15), accessToken.ValidTo - accessToken.IssuedAt);
        DateTimeOffset refreshExpiresAt = body.RootElement.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset();
        DateTimeOffset accessExpiresAt = body.RootElement.GetProperty("accessTokenExpiresAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(15), refreshExpiresAt - accessExpiresAt);
        Assert.Equal("Bearer", body.RootElement.GetProperty("tokenType").GetString());
        Assert.Equal(1L, await CountAsync("SELECT count(DISTINCT chain_id) FROM authentication.refresh_tokens"));
        Assert.Equal(0, await QueryAsync<int>("SELECT failed_sign_in_count FROM authentication.users"));
    }

    [Fact]
    public async Task Login_ActiveOperator_TokenCarriesOperatorId()
    {
        // Arrange
        await SeedAccountAsync("Operator", "Active");

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com", Password);

        // Assert
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var accessToken = new JsonWebToken(body.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal("Operator", accessToken.GetClaim("role").Value);
        Assert.Equal(_userId.ToString(), accessToken.GetClaim("operatorId").Value);
    }

    [Fact]
    public async Task Login_UnknownEmailOrWrongPassword_AnswersInvalidCredentialsTheSameWay()
    {
        // Arrange
        await SeedAccountAsync("Customer", "Active");

        // Act
        using HttpResponseMessage unknown = await PostAsync("nobody@example.com", Password);
        using HttpResponseMessage wrong = await PostAsync("alice@example.com", "wrong-password-1");

        // Assert
        using JsonDocument unknownProblem = await AssertProblemAsync(unknown, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        using JsonDocument wrongProblem = await AssertProblemAsync(wrong, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(unknownProblem.RootElement.GetProperty("title").GetString(), wrongProblem.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("Customer", "PendingVerification", "EMAIL_NOT_VERIFIED")]
    [InlineData("Operator", "PendingApproval", "ACCOUNT_PENDING_APPROVAL")]
    [InlineData("Operator", "Rejected", "ACCOUNT_REJECTED")]
    [InlineData("Customer", "Suspended", "ACCOUNT_SUSPENDED")]
    public async Task Login_CorrectPasswordButAccountNotActive_IsRefusedWithItsReason(string role, string status, string code)
    {
        // Arrange
        await SeedAccountAsync(role, status);

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com", Password);

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.Forbidden, code);
    }

    [Theory]
    [InlineData("PendingVerification")]
    [InlineData("PendingApproval")]
    [InlineData("Rejected")]
    [InlineData("Suspended")]
    public async Task Login_WrongPasswordOnAnInactiveAccount_NeverRevealsTheStatusAndCountsTheFailure(string status)
    {
        // Arrange
        await SeedAccountAsync("Operator", status);

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com", "wrong-password-1");

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(1, await QueryAsync<int>("SELECT failed_sign_in_count FROM authentication.users"));
    }

    [Fact]
    public async Task Login_FifthFailureWithinTenMinutes_LocksForFifteenMinutesAndLogsIt()
    {
        // Arrange
        await SeedAccountAsync("Customer", "Active");
        await SetLockoutAsync(4, "now() - interval '6 minutes'", "NULL");

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com", "wrong-password-1");

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_LOCKED");
        Assert.True(problem.RootElement.TryGetProperty("lockedUntil", out _));
        Assert.True(await QueryAsync<bool>(
            "SELECT locked_until BETWEEN now() + interval '14 minutes' AND now() + interval '16 minutes' FROM authentication.users"));
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("locked until"));
    }

    [Fact]
    public async Task Login_LockedAccountWithTheRightPassword_IsStillRefusedAndTheLockIsNotExtended()
    {
        // Arrange
        await SeedAccountAsync("Customer", "Active");
        await SetLockoutAsync(5, "now() - interval '5 minutes'", "date_trunc('second', now()) + interval '10 minutes'");
        DateTime lockedUntil = await QueryAsync<DateTime>("SELECT locked_until FROM authentication.users");

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com", Password);

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ACCOUNT_LOCKED");
        Assert.Equal(lockedUntil, problem.RootElement.GetProperty("lockedUntil").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(lockedUntil, await QueryAsync<DateTime>("SELECT locked_until FROM authentication.users"));
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("refused for locked account"));
    }

    [Fact]
    public async Task Login_AfterTheLockEnds_SignsInAndResetsTheCount()
    {
        // Arrange
        await SeedAccountAsync("Customer", "Active");
        await SetLockoutAsync(5, "now() - interval '16 minutes'", "now() - interval '1 minute'");

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com", Password);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await QueryAsync<int>("SELECT failed_sign_in_count FROM authentication.users"));
    }

    [Fact]
    public async Task Login_FailuresStartedOutsideTheWindow_DoNotAddUpToALock()
    {
        // Arrange
        await SeedAccountAsync("Customer", "Active");
        await SetLockoutAsync(4, "now() - interval '11 minutes'", "NULL");

        // Act
        using HttpResponseMessage response = await PostAsync("alice@example.com", "wrong-password-1");

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(1, await QueryAsync<int>("SELECT failed_sign_in_count FROM authentication.users"));
    }

    [Theory]
    [InlineData("not-an-email", Password)]
    [InlineData("alice@example.com", "")]
    public async Task Login_MalformedRequest_IsRefusedAndCountsNothing(string email, string password)
    {
        // Arrange
        await SeedAccountAsync("Customer", "Active");

        // Act
        using HttpResponseMessage response = await PostAsync(email, password);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await QueryAsync<int>("SELECT failed_sign_in_count FROM authentication.users"));
    }

    private static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private async Task<HttpResponseMessage> PostAsync(string email, string password)
    {
        using ApiFactory factory = IdentityTestHost.For(
            _database,
            services => services.AddSingleton<ISigningKeyVault>(_vault),
            services => services.AddSingleton<ILoggerProvider>(_logs));
        using HttpClient client = factory.CreateClient();
        return await client.PostAsJsonAsync(Route, new { email, password });
    }

    private Task<long> CountAsync(string sql)
    {
        return _database.QueryScalarAsync<long>(sql);
    }

    private async Task<T> QueryAsync<T>(string sql)
    {
        T? value = await _database.QueryScalarAsync<T>(sql);
        return value!;
    }

    private async Task SeedAccountAsync(string role, string status)
    {
        await _database.SeedAsync(
            """
            INSERT INTO authentication.users (user_id, email, password_hash, full_name, role, status, failed_sign_in_count)
            VALUES (@UserId, 'alice@example.com', @PasswordHash, 'Alice Rao', @Role, @Status, 0)
            """,
            new { UserId = _userId, PasswordHash = _passwordHash, Role = role, Status = status });
    }

    private async Task SetLockoutAsync(int failedCount, string firstFailedAt, string lockedUntil)
    {
        await _database.SeedAsync(
            $"""
            UPDATE authentication.users
            SET failed_sign_in_count = @FailedCount, first_failed_sign_in_at = {firstFailedAt}, locked_until = {lockedUntil}
            """,
            new { FailedCount = failedCount });
    }
}
