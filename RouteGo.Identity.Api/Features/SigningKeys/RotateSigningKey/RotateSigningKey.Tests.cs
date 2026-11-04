using System.Security.Cryptography;
using System.Text.Json;
using RouteGo.Identity.Api.Features.SigningKeys.RotateSigningKey;
using RouteGo.Identity.Api.Infrastructure.Pgsql;
using RouteGo.Identity.Api.Tests.Database;
using RouteGo.Identity.Api.Tests.Support;

namespace RouteGo.Identity.Api.Tests.Features.SigningKeys.RotateSigningKey;

[Collection(DatabaseCollection.Name)]
public sealed class RotateSigningKeyTests : IAsyncLifetime
{
    private const string OldKey = "k-2026-09";
    private const string NewKey = "k-2026-10";

    private const string InsertKeySql = """
        INSERT INTO signing_keys.signing_keys (key_id, public_key, state, introduced_at, introduced_by, superseded_at)
        VALUES (@KeyId, 'public-key-pem', @State, @IntroducedAt, 'instructor', @SupersededAt)
        """;

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;
    private readonly InMemorySigningKeyVault _vault = new InMemorySigningKeyVault();
    private readonly RotateSigningKeyHandler _handler;

    public RotateSigningKeyTests(DatabaseFixture database)
    {
        _database = database;
        var writerFactory = new ActorWriterConnectionFactory(database.ConnectionString, ConnectionRetryOptions.ManagedInstances);
        var settings = new SigningKeyRotationSettings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));
        _handler = new RotateSigningKeyHandler(writerFactory, _vault, new FixedClock(_now), settings);
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
    public async Task HandleAsync_IntroduceNewKey_PublishesItWithoutSigningAndRecordsTheAudit()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Active", _now.AddDays(-30), null);
        _vault.AddNewKey(NewKey);

        // Act
        RotateSigningKeyResult result = await RunAsync("introduce", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.Completed, result.Outcome);
        Assert.Equal("Introduced", await GetStateAsync(NewKey));
        Assert.Equal("Active", await GetStateAsync(OldKey));
        string? publicKey = await _database.QueryScalarAsync<string>(
            "SELECT public_key FROM signing_keys.signing_keys WHERE key_id = @KeyId", new { KeyId = NewKey });
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", publicKey);
        using JsonDocument audit = await GetSingleAuditAsync();
        Assert.Equal("SigningKeyIntroduced", audit.RootElement.GetProperty("action").GetString());
        Assert.Equal("instructor", audit.RootElement.GetProperty("actorId").GetString());
        Assert.Equal("PlatformEngineer", audit.RootElement.GetProperty("actorRole").GetString());
        Assert.Equal(NewKey, audit.RootElement.GetProperty("details").GetProperty("keyId").GetString());
    }

    [Fact]
    public async Task HandleAsync_ActivateAfterTheWait_SwitchesSigningAndKeepsTheOldKeyPublished()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Active", _now.AddDays(-30), null);
        await SeedKeyAsync(NewKey, "Introduced", _now.AddMinutes(-11), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("activate", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.Completed, result.Outcome);
        Assert.Equal("Active", await GetStateAsync(NewKey));
        Assert.Equal("Previous", await GetStateAsync(OldKey));
        using JsonDocument audit = await GetSingleAuditAsync();
        Assert.Equal("SigningKeyActivated", audit.RootElement.GetProperty("action").GetString());
        Assert.Equal(OldKey, audit.RootElement.GetProperty("details").GetProperty("previouslyActiveKeyId").GetString());
    }

    [Fact]
    public async Task HandleAsync_ActivateTooEarly_IsRefusedWithTheTimeItBecomesAllowed()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Active", _now.AddDays(-30), null);
        await SeedKeyAsync(NewKey, "Introduced", _now.AddMinutes(-4), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("activate", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.NotYetPropagated, result.Outcome);
        Assert.Equal(_now.AddMinutes(6), result.AllowedAt);
        Assert.Equal("Active", await GetStateAsync(OldKey));
        Assert.Equal(0L, await CountAuditEventsAsync());
    }

    [Fact]
    public async Task HandleAsync_RetireAfterOneTokenLifetime_RemovesTheKeyFromPublication()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Previous", _now.AddDays(-30), _now.AddMinutes(-16));
        await SeedKeyAsync(NewKey, "Active", _now.AddMinutes(-30), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("retire", OldKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.Completed, result.Outcome);
        Assert.Equal("Retired", await GetStateAsync(OldKey));
        using JsonDocument audit = await GetSingleAuditAsync();
        Assert.Equal("SigningKeyRetired", audit.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task HandleAsync_RetireTooEarly_IsRefusedWithTheTimeItBecomesAllowed()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Previous", _now.AddDays(-30), _now.AddMinutes(-10));
        await SeedKeyAsync(NewKey, "Active", _now.AddMinutes(-30), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("retire", OldKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.RetirementTooEarly, result.Outcome);
        Assert.Equal(_now.AddMinutes(5), result.AllowedAt);
        Assert.Equal("Previous", await GetStateAsync(OldKey));
    }

    [Fact]
    public async Task HandleAsync_RetireTheActiveKey_IsNotAllowedAndCarriesItsState()
    {
        // Arrange
        await SeedKeyAsync(NewKey, "Active", _now.AddMinutes(-30), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("retire", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.StageNotAllowed, result.Outcome);
        Assert.Equal("Active", result.CurrentState);
        Assert.Equal("Active", await GetStateAsync(NewKey));
    }

    [Fact]
    public async Task HandleAsync_ActivateAKeyNeverIntroduced_IsNotAllowed()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Active", _now.AddDays(-30), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("activate", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.StageNotAllowed, result.Outcome);
        Assert.Null(result.CurrentState);
    }

    [Fact]
    public async Task HandleAsync_IntroduceWhileAnotherKeyIsIntroduced_IsNotAllowed()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Introduced", _now.AddMinutes(-1), null);
        _vault.AddNewKey(NewKey);

        // Act
        RotateSigningKeyResult result = await RunAsync("introduce", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.StageNotAllowed, result.Outcome);
        Assert.Null(await GetStateAsync(NewKey));
    }

    [Fact]
    public async Task HandleAsync_ActivateRunTwice_ChangesNothingTheSecondTime()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Active", _now.AddDays(-30), null);
        await SeedKeyAsync(NewKey, "Introduced", _now.AddMinutes(-11), null);
        await RunAsync("activate", NewKey);

        // Act
        RotateSigningKeyResult second = await RunAsync("activate", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.AlreadyDone, second.Outcome);
        Assert.Equal("Active", await GetStateAsync(NewKey));
        Assert.Equal(1L, await CountAuditEventsAsync());
    }

    [Fact]
    public async Task HandleAsync_IntroduceAKeyAlreadyActive_IsNotAllowedAndCarriesItsState()
    {
        // Arrange
        await SeedKeyAsync(NewKey, "Active", _now.AddMinutes(-30), null);
        _vault.AddNewKey(NewKey);

        // Act
        RotateSigningKeyResult result = await RunAsync("introduce", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.StageNotAllowed, result.Outcome);
        Assert.Equal("Active", result.CurrentState);
        Assert.Equal("Active", await GetStateAsync(NewKey));
        Assert.Equal(0L, await CountAuditEventsAsync());
    }

    [Fact]
    public async Task HandleAsync_ActivateAPreviousKey_IsNotAllowedAndCarriesItsState()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Previous", _now.AddDays(-30), _now.AddMinutes(-5));
        await SeedKeyAsync(NewKey, "Active", _now.AddMinutes(-30), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("activate", OldKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.StageNotAllowed, result.Outcome);
        Assert.Equal("Previous", result.CurrentState);
        Assert.Equal("Previous", await GetStateAsync(OldKey));
        Assert.Equal("Active", await GetStateAsync(NewKey));
    }

    [Fact]
    public async Task HandleAsync_FirstKeyWithNoActiveKey_ActivatesWithoutTheWait()
    {
        // Arrange
        await SeedKeyAsync(NewKey, "Introduced", _now.AddMinutes(-1), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("activate", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.Completed, result.Outcome);
        Assert.Equal("Active", await GetStateAsync(NewKey));
    }

    [Fact]
    public async Task HandleAsync_IntroduceWithNoPrivateKeyInTheStore_IsRefusedAndPublishesNothing()
    {
        // Arrange
        await SeedKeyAsync(OldKey, "Active", _now.AddDays(-30), null);

        // Act
        RotateSigningKeyResult result = await RunAsync("introduce", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.PrivateKeyUnusable, result.Outcome);
        Assert.Null(await GetStateAsync(NewKey));
        Assert.Equal(0L, await CountAuditEventsAsync());
    }

    [Fact]
    public async Task HandleAsync_IntroduceWithAMalformedSecret_IsRefused()
    {
        // Arrange
        _vault.Put(NewKey, "not a pem");

        // Act
        RotateSigningKeyResult result = await RunAsync("introduce", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.PrivateKeyUnusable, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_IntroduceWithAPemWhoseBodyIsCorrupt_IsRefused()
    {
        // Arrange
        _vault.Put(NewKey, "-----BEGIN PRIVATE KEY-----\nAAAAAAAA\n-----END PRIVATE KEY-----");

        // Act
        RotateSigningKeyResult result = await RunAsync("introduce", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.PrivateKeyUnusable, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_IntroduceWithOnlyAPublicKeyStored_IsRefusedAndPublishesNothing()
    {
        // Arrange
        using var rsa = RSA.Create(2048);
        _vault.Put(NewKey, rsa.ExportSubjectPublicKeyInfoPem());

        // Act
        RotateSigningKeyResult result = await RunAsync("introduce", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.PrivateKeyUnusable, result.Outcome);
        Assert.Contains("only a public key", result.Reason);
        Assert.Null(await GetStateAsync(NewKey));
    }

    [Fact]
    public async Task HandleAsync_IntroduceWithA4096BitKey_IsRefused()
    {
        // Arrange
        using var rsa = RSA.Create(4096);
        _vault.Put(NewKey, rsa.ExportPkcs8PrivateKeyPem());

        // Act
        RotateSigningKeyResult result = await RunAsync("introduce", NewKey);

        // Assert
        Assert.Equal(RotateSigningKeyOutcome.PrivateKeyUnusable, result.Outcome);
        Assert.Contains("4096-bit", result.Reason);
    }

    private Task<RotateSigningKeyResult> RunAsync(string stage, string keyId)
    {
        var request = new RotateSigningKeyRequest(stage, keyId, "instructor");
        return _handler.HandleAsync(request, "test-correlation", CancellationToken.None);
    }

    private async Task SeedKeyAsync(string keyId, string state, DateTimeOffset introducedAt, DateTimeOffset? supersededAt)
    {
        await _database.SeedAsync(InsertKeySql, new
        {
            KeyId = keyId,
            State = state,
            IntroducedAt = introducedAt,
            SupersededAt = supersededAt,
        });
    }

    private Task<string?> GetStateAsync(string keyId)
    {
        return _database.QueryScalarAsync<string>(
            "SELECT state FROM signing_keys.signing_keys WHERE key_id = @KeyId", new { KeyId = keyId });
    }

    private async Task<long> CountAuditEventsAsync()
    {
        return await _database.QueryScalarAsync<long>(
            "SELECT count(*) FROM authentication.identity_events WHERE event_type = 'AuditRecorded'");
    }

    private async Task<JsonDocument> GetSingleAuditAsync()
    {
        string? data = await _database.QueryScalarAsync<string>(
            "SELECT data FROM authentication.identity_events WHERE event_type = 'AuditRecorded'");
        return JsonDocument.Parse(data!);
    }
}
