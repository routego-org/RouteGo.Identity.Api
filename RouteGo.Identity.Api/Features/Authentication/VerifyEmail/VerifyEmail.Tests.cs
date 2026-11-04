using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.Authentication.VerifyEmail;

[Collection(DatabaseCollection.Name)]
public sealed class VerifyEmailTests : IAsyncLifetime
{
    private const string Route = "/auth/emailVerifications";
    private const string Token = "test-verification-token";

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private string _publicKeyPem = string.Empty;

    public VerifyEmailTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        _publicKeyPem = await TestSigningKeys.SeedActiveAsync(_database, _vault);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task VerifyEmail_CustomerWithFreshLink_BecomesActiveAndIsSignedIn()
    {
        // Arrange
        Guid userId = await SeedAccountWithLinkAsync("Customer", "interval '2 hours'", null);

        // Act
        using HttpResponseMessage response = await PostAsync(Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("active", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("customer", body.RootElement.GetProperty("role").GetString());
        string accessToken = body.RootElement.GetProperty("tokens").GetProperty("accessToken").GetString()!;
        TokenValidationResult validation = await ValidateAsync(accessToken);
        Assert.True(validation.IsValid);
        Assert.Equal(userId.ToString(), validation.Claims["sub"]);
        Assert.Equal("Customer", validation.Claims["role"]);
        Assert.Equal("Active", await QueryAsync<string>("SELECT status FROM authentication.users"));
        Assert.True(await QueryAsync<bool>("SELECT email_verified_at IS NOT NULL FROM authentication.users"));
        Assert.True(await QueryAsync<bool>("SELECT used_at IS NOT NULL FROM authentication.email_verification_tokens"));
        Assert.Equal(1L, await QueryAsync<long>("SELECT count(*) FROM authentication.refresh_tokens"));
    }

    [Fact]
    public async Task VerifyEmail_OperatorWithFreshLink_AccountAndApplicationAwaitApprovalWithNoTokens()
    {
        // Arrange
        Guid operatorId = await SeedAccountWithLinkAsync("Operator", "interval '2 hours'", null);
        await _database.SeedAsync(
            """
            INSERT INTO operator_onboarding.operator_applications (operator_id, company_name, contact_email, gstin, status, submitted_at)
            VALUES (@OperatorId, 'BusCo Travels', 'bookings@busco.in', '27AAPFU0939F1ZV', 'PendingVerification', now())
            """,
            new { OperatorId = operatorId });

        // Act
        using HttpResponseMessage response = await PostAsync(Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pendingApproval", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("tokens").ValueKind);
        Assert.Equal("PendingApproval", await QueryAsync<string>("SELECT status FROM authentication.users"));
        Assert.Equal("PendingApproval", await QueryAsync<string>("SELECT status FROM operator_onboarding.operator_applications"));
        Assert.Equal(0L, await QueryAsync<long>("SELECT count(*) FROM authentication.refresh_tokens"));
    }

    [Fact]
    public async Task VerifyEmail_LinkIssued25HoursAgo_IsRefusedAsExpired()
    {
        // Arrange
        await SeedAccountWithLinkAsync("Customer", "interval '25 hours'", null);

        // Act
        using HttpResponseMessage response = await PostAsync(Token);

        // Assert
        await AssertProblemAsync(response, "VERIFICATION_LINK_EXPIRED");
        Assert.Equal("PendingVerification", await QueryAsync<string>("SELECT status FROM authentication.users"));
    }

    [Fact]
    public async Task VerifyEmail_LinkAlreadyUsed_IsRefusedCarryingTheAccountStatus()
    {
        // Arrange
        await SeedAccountWithLinkAsync("Customer", "interval '2 hours'", null);
        using HttpResponseMessage first = await PostAsync(Token);

        // Act
        using HttpResponseMessage response = await PostAsync(Token);

        // Assert
        using JsonDocument problem = await AssertProblemAsync(response, "VERIFICATION_LINK_ALREADY_USED");
        Assert.Equal("active", problem.RootElement.GetProperty("accountStatus").GetString());
        Assert.Equal(1L, await QueryAsync<long>("SELECT count(*) FROM authentication.refresh_tokens"));
    }

    [Fact]
    public async Task VerifyEmail_TokenNeverIssued_IsRefusedAsInvalid()
    {
        // Arrange
        await SeedAccountWithLinkAsync("Customer", "interval '2 hours'", null);

        // Act
        using HttpResponseMessage response = await PostAsync("a-token-identity-never-issued");

        // Assert
        await AssertProblemAsync(response, "VERIFICATION_LINK_INVALID");
    }

    [Fact]
    public async Task VerifyEmail_LinkReplacedByANewerOne_IsRefusedAsInvalid()
    {
        // Arrange
        await SeedAccountWithLinkAsync("Customer", "interval '2 hours'", "now()");

        // Act
        using HttpResponseMessage response = await PostAsync(Token);

        // Assert
        await AssertProblemAsync(response, "VERIFICATION_LINK_INVALID");
    }

    [Fact]
    public async Task VerifyEmail_EmptyToken_IsRefusedAsValidationFailed()
    {
        // Act
        using HttpResponseMessage response = await PostAsync(string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private async Task<TokenValidationResult> ValidateAsync(string accessToken)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_publicKeyPem);
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "http://localhost:5001",
            ValidAudience = "routego",
            IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = TestSigningKeys.ActiveKeyId },
        };
        return await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, parameters);
    }

    private async Task<HttpResponseMessage> PostAsync(string token)
    {
        using ApiFactory factory = IdentityTestHost.For(_database, services => services.AddSingleton<ISigningKeyVault>(_vault));
        using HttpClient client = factory.CreateClient();
        return await client.PostAsJsonAsync(Route, new { token });
    }

    private Task<T?> QueryAsync<T>(string sql)
    {
        return _database.QueryScalarAsync<T>(sql);
    }

    private async Task<Guid> SeedAccountWithLinkAsync(string role, string issuedAgo, string? voidedAt)
    {
        var userId = Guid.NewGuid();
        string voided = voidedAt ?? "NULL";
        await _database.SeedAsync(
            $"""
            INSERT INTO authentication.users (user_id, email, password_hash, full_name, role, status, failed_sign_in_count)
            VALUES (@UserId, 'alice@example.com', 'hash', 'Alice Rao', @Role, 'PendingVerification', 0);
            INSERT INTO authentication.email_verification_tokens (user_id, token_hash, issued_at, expires_at, voided_at)
            VALUES (@UserId, @TokenHash, now() - {issuedAgo}, now() - {issuedAgo} + interval '24 hours', {voided});
            """,
            new { UserId = userId, Role = role, TokenHash = OpaqueToken.Hash(Token) });
        return userId;
    }
}
