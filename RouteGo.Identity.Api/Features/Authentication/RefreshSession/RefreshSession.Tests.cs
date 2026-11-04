using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.RefreshSession;

[Collection(DatabaseCollection.Name)]
public sealed class RefreshSessionTests : IAsyncLifetime
{
    private const string Route = "/auth/sessions/refresh";
    private const string Email = "alice@example.com";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private readonly CapturingLoggerProvider _logs = new CapturingLoggerProvider();
    private ApiFactory? _factory;
    private HttpClient? _client;

    public RefreshSessionTests(DatabaseFixture database)
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
        await TestSessions.SeedAccountAsync(_database, Email, "Customer", "Active");
        _factory = IdentityTestHost.For(
            _database,
            services => services.AddSingleton<ISigningKeyVault>(_vault),
            services => services.AddSingleton<ILoggerProvider>(_logs));
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RefreshSession_UnusedToken_RotatesWithinTheSameChain()
    {
        // Arrange
        TestSession signIn = await TestSessions.SignInAsync(Client, Email);

        // Act
        using HttpResponseMessage response = await RefreshAsync(signIn.RefreshToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEqual(signIn.RefreshToken, body.RootElement.GetProperty("refreshToken").GetString());
        Assert.Equal(1L, await CountAsync("SELECT count(DISTINCT chain_id) FROM authentication.refresh_tokens"));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM authentication.refresh_tokens WHERE replaced_by IS NOT NULL"));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM authentication.refresh_tokens WHERE expires_at - issued_at = interval '7 days'"));
    }

    [Fact]
    public async Task RefreshSession_AlreadyExchangedToken_RevokesTheChainButNotOtherSessions()
    {
        // Arrange
        TestSession laptop = await TestSessions.SignInAsync(Client, Email);
        TestSession phone = await TestSessions.SignInAsync(Client, Email);
        string second = await RefreshTokenOfAsync(laptop.RefreshToken);
        string third = await RefreshTokenOfAsync(second);

        // Act
        using HttpResponseMessage response = await RefreshAsync(laptop.RefreshToken);

        // Assert
        await AssertProblemAsync(response, "REFRESH_TOKEN_REUSED");
        using HttpResponseMessage afterReuse = await RefreshAsync(third);
        await AssertProblemAsync(afterReuse, "REFRESH_TOKEN_INVALID");
        using HttpResponseMessage otherDevice = await RefreshAsync(phone.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, otherDevice.StatusCode);
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("reuse detected"));
    }

    [Fact]
    public async Task RefreshSession_TokenNeverIssued_IsInvalid()
    {
        // Act
        using HttpResponseMessage response = await RefreshAsync("a-token-identity-never-issued");

        // Assert
        await AssertProblemAsync(response, "REFRESH_TOKEN_INVALID");
    }

    [Fact]
    public async Task RefreshSession_ExpiredToken_IsInvalid()
    {
        // Arrange
        TestSession signIn = await TestSessions.SignInAsync(Client, Email);
        await _database.SeedAsync("UPDATE authentication.refresh_tokens SET expires_at = now() - interval '1 minute'");

        // Act
        using HttpResponseMessage response = await RefreshAsync(signIn.RefreshToken);

        // Assert
        await AssertProblemAsync(response, "REFRESH_TOKEN_INVALID");
    }

    [Fact]
    public async Task RefreshSession_TokenRevokedBySuspension_IsInvalid()
    {
        // Arrange
        TestSession signIn = await TestSessions.SignInAsync(Client, Email);
        await _database.SeedAsync(
            "UPDATE authentication.refresh_tokens SET revoked_at = now(), revoke_reason = 'AccountSuspended'");
        await _database.SeedAsync("UPDATE authentication.users SET status = 'Suspended'");

        // Act
        using HttpResponseMessage response = await RefreshAsync(signIn.RefreshToken);

        // Assert
        await AssertProblemAsync(response, "REFRESH_TOKEN_INVALID");
    }

    [Fact]
    public async Task RefreshSession_EmptyToken_IsRefusedAsValidationFailed()
    {
        // Act
        using HttpResponseMessage response = await RefreshAsync(string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken)
    {
        return Client.PostAsJsonAsync(Route, new { refreshToken });
    }

    private async Task<string> RefreshTokenOfAsync(string refreshToken)
    {
        using HttpResponseMessage response = await RefreshAsync(refreshToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("refreshToken").GetString()!;
    }

    private Task<long> CountAsync(string sql)
    {
        return _database.QueryScalarAsync<long>(sql);
    }
}
