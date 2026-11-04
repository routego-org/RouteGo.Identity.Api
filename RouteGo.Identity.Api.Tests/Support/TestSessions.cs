using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Tests.Database;

namespace RouteGo.Identity.Api.Tests.Support;

/// <summary>A session obtained through the real sign-in endpoint.</summary>
/// <param name="AccessToken">The access token.</param>
/// <param name="RefreshToken">The refresh token.</param>
public sealed record TestSession(string AccessToken, string RefreshToken);

/// <summary>Accounts and sessions for tests that need a signed-in caller.</summary>
public static class TestSessions
{
    /// <summary>The password every seeded account has.</summary>
    public const string Password = "travel2026x";

    private static readonly string _passwordHash = PasswordHashing.Hash(Password);

    /// <summary>Seeds an account that can sign in with <see cref="Password"/>.</summary>
    public static async Task<Guid> SeedAccountAsync(DatabaseFixture database, string email, string role, string status)
    {
        var userId = Guid.NewGuid();
        await database.SeedAsync(
            """
            INSERT INTO authentication.users (user_id, email, password_hash, full_name, role, status, failed_sign_in_count)
            VALUES (@UserId, @Email, @PasswordHash, 'Test Person', @Role, @Status, 0)
            """,
            new { UserId = userId, Email = email, PasswordHash = _passwordHash, Role = role, Status = status });
        return userId;
    }

    /// <summary>Signs in through POST /auth/sessions.</summary>
    public static async Task<TestSession> SignInAsync(HttpClient client, string email)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/sessions", new { email, password = Password });
        string content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Test sign-in for {email} failed with {(int)response.StatusCode}: {content}");
        }

        using var body = JsonDocument.Parse(content);
        return new TestSession(
            body.RootElement.GetProperty("accessToken").GetString()!,
            body.RootElement.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>A request carrying the access token as a bearer credential.</summary>
    public static HttpRequestMessage Authorized(HttpMethod method, string route, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>A genuine access token, signed with the active test key, that expired a minute ago.</summary>
    public static async Task<string> CreateExpiredAccessTokenAsync(ISigningKeyVault vault, Guid userId)
    {
        string? pem = await vault.GetPrivateKeyPemAsync(TestSigningKeys.ActiveKeyId, CancellationToken.None);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var key = new RsaSecurityKey(rsa) { KeyId = TestSigningKeys.ActiveKeyId };
        var expiredAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "http://localhost:5001",
            Audience = "routego",
            IssuedAt = expiredAt.AddMinutes(-15),
            NotBefore = expiredAt.AddMinutes(-15),
            Expires = expiredAt,
            Claims = new Dictionary<string, object> { ["sub"] = userId.ToString(), ["email"] = "alice@example.com", ["role"] = "Customer" },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
