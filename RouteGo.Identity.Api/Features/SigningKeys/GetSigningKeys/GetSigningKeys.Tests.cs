using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RouteGo.Identity.Api.Common.Ports;
using RouteGo.Identity.Api.Common.Security;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.SigningKeys.GetSigningKeys;

[Collection(DatabaseCollection.Name)]
public sealed class GetSigningKeysTests : IAsyncLifetime
{
    private const string Route = "/.well-known/jwks.json";

    private const string InsertKeySql = """
        INSERT INTO signing_keys.signing_keys (key_id, public_key, state, introduced_at, introduced_by)
        VALUES (@KeyId, @PublicKey, @State, @IntroducedAt, 'instructor')
        """;

    private static readonly DateTimeOffset _baseTime = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public GetSigningKeysTests(DatabaseFixture database)
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
    public async Task GetSigningKeys_ActiveAndIntroducedKeys_ListsBothAsRs256SigningKeys()
    {
        // Arrange
        await SeedKeyAsync("k-2026-09", "Active", -30);
        await SeedKeyAsync("k-2026-10", "Introduced", -1);

        // Act
        using JsonDocument keySet = await GetKeySetAsync();

        // Assert
        JsonElement keys = keySet.RootElement.GetProperty("keys");
        Assert.Equal(new[] { "k-2026-09", "k-2026-10" }, keys.EnumerateArray().Select(key => key.GetProperty("kid").GetString()));
        Assert.All(keys.EnumerateArray(), key => Assert.Equal("RSA", key.GetProperty("kty").GetString()));
        Assert.All(keys.EnumerateArray(), key => Assert.Equal("sig", key.GetProperty("use").GetString()));
        Assert.All(keys.EnumerateArray(), key => Assert.Equal("RS256", key.GetProperty("alg").GetString()));
    }

    [Fact]
    public async Task GetSigningKeys_PreviousKey_StaysPublishedUntilRetired()
    {
        // Arrange
        await SeedKeyAsync("k-2026-09", "Previous", -30);
        await SeedKeyAsync("k-2026-10", "Active", -20);

        // Act
        using JsonDocument keySet = await GetKeySetAsync();

        // Assert
        Assert.Contains("k-2026-09", KeyIds(keySet));
    }

    [Fact]
    public async Task GetSigningKeys_RetiredKey_IsNotPublished()
    {
        // Arrange
        await SeedKeyAsync("k-2026-09", "Retired", -30);
        await SeedKeyAsync("k-2026-10", "Active", -20);

        // Act
        using JsonDocument keySet = await GetKeySetAsync();

        // Assert
        Assert.DoesNotContain("k-2026-09", KeyIds(keySet));
    }

    [Fact]
    public async Task GetSigningKeys_AnyKey_CarriesNoPrivateComponent()
    {
        // Arrange
        await SeedKeyAsync("k-2026-10", "Active", -20);

        // Act
        using JsonDocument keySet = await GetKeySetAsync();

        // Assert
        JsonElement key = keySet.RootElement.GetProperty("keys")[0];
        string[] names = key.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "kty", "use", "alg", "kid", "n", "e" }, names);
    }

    [Fact]
    public async Task GetSigningKeys_WithoutAnAccessToken_IsReturned()
    {
        // Arrange
        await SeedKeyAsync("k-2026-10", "Active", -20);
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync(Route);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSigningKeys_AnyFetch_MayBeCachedForAtMostFiveMinutes()
    {
        // Arrange
        await SeedKeyAsync("k-2026-10", "Active", -20);
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync(Route);

        // Assert
        Assert.Equal(new[] { "public, max-age=300" }, response.Headers.GetValues("Cache-Control"));
    }

    [Fact]
    public async Task GetSigningKeys_NoKeyIntroducedYet_ReturnsAnEmptySet()
    {
        // Act
        using JsonDocument keySet = await GetKeySetAsync();

        // Assert
        Assert.Empty(KeyIds(keySet));
    }

    [Fact]
    public async Task GetSigningKeys_TokenFromSignIn_ValidatesWithTheKeySetAloneAndATamperedCopyFails()
    {
        // Arrange
        var vault = new InMemorySigningKeyVault();
        await TestSigningKeys.SeedActiveAsync(_database, vault);
        await _database.SeedAsync(
            """
            INSERT INTO authentication.users (email, password_hash, full_name, role, status, failed_sign_in_count)
            VALUES ('alice@example.com', @PasswordHash, 'Alice Rao', 'Customer', 'Active', 0)
            """,
            new { PasswordHash = PasswordHashing.Hash("travel2026x") });
        using ApiFactory factory = IdentityTestHost.For(_database, services => services.AddSingleton<ISigningKeyVault>(vault));
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage signIn = await client.PostAsJsonAsync("/auth/sessions", new { email = "alice@example.com", password = "travel2026x" });
        using var session = JsonDocument.Parse(await signIn.Content.ReadAsStringAsync());
        string accessToken = session.RootElement.GetProperty("accessToken").GetString()!;
        var keySet = new JsonWebKeySet(await client.GetStringAsync(Route));
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "http://localhost:5001",
            ValidAudience = "routego",
            IssuerSigningKeys = keySet.GetSigningKeys(),
        };
        string tampered = TamperRoleClaim(accessToken);

        // Act
        TokenValidationResult genuine = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, parameters);
        TokenValidationResult forged = await new JsonWebTokenHandler().ValidateTokenAsync(tampered, parameters);

        // Assert
        Assert.True(genuine.IsValid);
        Assert.False(forged.IsValid);
    }

    // Rewrites the payload to claim role Admin while keeping the original signature.
    private static string TamperRoleClaim(string accessToken)
    {
        string[] parts = accessToken.Split('.');
        string payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        string forgedPayload = payload.Replace("\"Customer\"", "\"Admin\"", StringComparison.Ordinal);
        return parts[0] + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(forgedPayload)) + "." + parts[2];
    }

    private static string?[] KeyIds(JsonDocument keySet)
    {
        return keySet.RootElement.GetProperty("keys").EnumerateArray().Select(key => key.GetProperty("kid").GetString()).ToArray();
    }

    private async Task<JsonDocument> GetKeySetAsync()
    {
        using ApiFactory factory = IdentityTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        string body = await client.GetStringAsync(Route);
        return JsonDocument.Parse(body);
    }

    private async Task SeedKeyAsync(string keyId, string state, int introducedMinutesAgo)
    {
        using var rsa = RSA.Create(2048);
        await _database.SeedAsync(InsertKeySql, new
        {
            KeyId = keyId,
            PublicKey = rsa.ExportSubjectPublicKeyInfoPem(),
            State = state,
            IntroducedAt = _baseTime.AddMinutes(introducedMinutesAgo),
        });
    }
}
