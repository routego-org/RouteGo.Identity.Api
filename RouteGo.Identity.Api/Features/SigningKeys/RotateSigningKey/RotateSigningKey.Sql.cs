using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.SigningKeys.RotateSigningKey;

/// <summary>The state changes each completed stage writes, and the audit record that goes with it.</summary>
public sealed partial class RotateSigningKeyHandler
{
    private const string PlatformEngineerRole = "PlatformEngineer";
    private const string SigningKeyEntity = "SigningKey";

    private const string InsertIntroducedSql = """
        INSERT INTO signing_keys.signing_keys (key_id, public_key, state, introduced_at, introduced_by)
        VALUES (@KeyId, @PublicKey, @State, @Now, @PerformedBy)
        """;

    private const string SupersedeSql = """
        UPDATE signing_keys.signing_keys SET state = @State, superseded_at = @Now WHERE key_id = @KeyId
        """;

    private const string ActivateSql = """
        UPDATE signing_keys.signing_keys
        SET state = @State, activated_at = @Now, activated_by = @PerformedBy
        WHERE key_id = @KeyId
        """;

    private const string RetireSql = """
        UPDATE signing_keys.signing_keys
        SET state = @State, retired_at = @Now, retired_by = @PerformedBy
        WHERE key_id = @KeyId
        """;

    private static async Task InsertIntroducedAsync(
        IWriterConnection writer, StageContext stage, string publicKeyPem, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            stage.KeyId,
            PublicKey = publicKeyPem,
            State = SigningKeyStates.Introduced,
            stage.Now,
            stage.PerformedBy,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertIntroducedSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    // The old key steps down first: the database allows only one active key at a time.
    private static async Task ActivateKeyAsync(
        IWriterConnection writer, StageContext stage, string? activeKeyId, CancellationToken cancellationToken)
    {
        if (activeKeyId != null)
        {
            var supersede = new { KeyId = activeKeyId, State = SigningKeyStates.Previous, stage.Now };
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                SupersedeSql, supersede, writer.Transaction, cancellationToken: cancellationToken));
        }

        var activate = new { stage.KeyId, State = SigningKeyStates.Active, stage.Now, stage.PerformedBy };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            ActivateSql, activate, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task RetireKeyAsync(IWriterConnection writer, StageContext stage, CancellationToken cancellationToken)
    {
        var retire = new { stage.KeyId, State = SigningKeyStates.Retired, stage.Now, stage.PerformedBy };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            RetireSql, retire, writer.Transaction, cancellationToken: cancellationToken));
    }

    // A key-rotation stage has no signed-in account: the actor is the name the engineer gives.
    private static async Task RecordAuditAsync(
        IWriterConnection writer, StageContext stage, string action, object details, CancellationToken cancellationToken)
    {
        var audit = new AuditRecordedData
        {
            ActorId = stage.PerformedBy,
            ActorRole = PlatformEngineerRole,
            Action = action,
            EntityType = SigningKeyEntity,
            EntityId = stage.KeyId,
            Details = details,
        };
        await IdentityEventRecorder.RecordAsync(
            writer, IdentityEventRecorder.AuditRecorded, audit, stage.CorrelationId, cancellationToken);
    }
}
