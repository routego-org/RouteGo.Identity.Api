using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Features.SigningKeys.RotateSigningKey;

/// <summary>One rotation stage a platform engineer runs: the stage, the key it applies to, and who runs it.</summary>
/// <param name="Stage">introduce, activate or retire.</param>
/// <param name="KeyId">The key ID the stage applies to.</param>
/// <param name="PerformedBy">The platform engineer's name, recorded as the audit actor.</param>
public sealed record RotateSigningKeyRequest(string? Stage, string? KeyId, string? PerformedBy);

/// <summary>The two waits the stages enforce.</summary>
/// <param name="MinimumActivationWait">Introduce-to-activate wait: the longest a consumer caches the key set.</param>
/// <param name="AccessTokenLifetime">Activate-to-retire wait: every token the old key signed has expired.</param>
public sealed record SigningKeyRotationSettings(TimeSpan MinimumActivationWait, TimeSpan AccessTokenLifetime);

/// <summary>How a stage ended.</summary>
public enum RotateSigningKeyOutcome
{
    Completed,
    AlreadyDone,
    NotYetPropagated,
    RetirementTooEarly,
    StageNotAllowed,
    PrivateKeyUnusable,
}

/// <summary>The outcome plus the facts the engineer is told.</summary>
public sealed class RotateSigningKeyResult
{
    public required RotateSigningKeyOutcome Outcome { get; init; }

    public required string KeyId { get; init; }

    /// <summary>The key's state when a stage did not fit it; null for a key that does not exist.</summary>
    public string? CurrentState { get; init; }

    /// <summary>When the refused stage may be re-run.</summary>
    public DateTimeOffset? AllowedAt { get; init; }

    /// <summary>Why the private half could not be used.</summary>
    public string? Reason { get; init; }
}

public sealed class RotateSigningKeyValidator : AbstractValidator<RotateSigningKeyRequest>
{
    public RotateSigningKeyValidator()
    {
        RuleFor(x => x.Stage).NotEmpty().Must(RotateSigningKeyHandler.IsKnownStage)
            .WithMessage("Stage must be introduce, activate or retire.");
        // The key ID becomes part of a Secrets Manager secret name, so it is held to the characters
        // a secret name accepts without escaping.
        RuleFor(x => x.KeyId).NotEmpty().Matches("^[A-Za-z0-9._-]+$");
        RuleFor(x => x.PerformedBy).NotEmpty();
    }
}

/// <summary>Runs one rotation stage; a stage and its audit record commit together or not at all.</summary>
public sealed partial class RotateSigningKeyHandler
{
    public const string Introduce = "introduce";
    public const string Activate = "activate";
    public const string Retire = "retire";

    private const string AuditAction = "job:RotateSigningKey";

    // Stages serialize on one lock so two engineers cannot interleave, for example, two activates.
    private const long RotationLockKey = 4_711_002;

    private const string LockSql = "SELECT pg_advisory_xact_lock(@Key)";

    private const string SelectKeysSql = """
        SELECT key_id, state, introduced_at, superseded_at
        FROM signing_keys.signing_keys
        FOR UPDATE
        """;

    private readonly IActorWriterConnectionFactory _writerFactory;
    private readonly ISigningKeyVault _vault;
    private readonly IClock _clock;
    private readonly SigningKeyRotationSettings _settings;

    public RotateSigningKeyHandler(
        IActorWriterConnectionFactory writerFactory, ISigningKeyVault vault, IClock clock, SigningKeyRotationSettings settings)
    {
        _writerFactory = writerFactory;
        _vault = vault;
        _clock = clock;
        _settings = settings;
    }

    public static bool IsKnownStage(string? stage)
    {
        return stage == Introduce || stage == Activate || stage == Retire;
    }

    /// <summary>Runs the stage named in the request, which the validator has already checked.</summary>
    public async Task<RotateSigningKeyResult> HandleAsync(
        RotateSigningKeyRequest request, string correlationId, CancellationToken cancellationToken)
    {
        var stage = new StageContext
        {
            KeyId = request.KeyId!,
            PerformedBy = request.PerformedBy!,
            CorrelationId = correlationId,
            Now = _clock.UtcNow,
        };

        await using IWriterConnection writer =
            await _writerFactory.OpenWriteTransactionAsync(ActorIdentity.System, AuditAction, cancellationToken);
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            LockSql, new { Key = RotationLockKey }, writer.Transaction, cancellationToken: cancellationToken));
        var keys = (await writer.Connection.QueryAsync<KeyRow>(new CommandDefinition(
            SelectKeysSql, transaction: writer.Transaction, cancellationToken: cancellationToken))).ToList();

        RotateSigningKeyResult result = request.Stage switch
        {
            Introduce => await IntroduceAsync(writer, keys, stage, cancellationToken),
            Activate => await ActivateAsync(writer, keys, stage, cancellationToken),
            _ => await RetireAsync(writer, keys, stage, cancellationToken),
        };

        if (result.Outcome == RotateSigningKeyOutcome.Completed)
        {
            await writer.CommitAsync(cancellationToken);
        }

        return result;
    }

    private async Task<RotateSigningKeyResult> IntroduceAsync(
        IWriterConnection writer, List<KeyRow> keys, StageContext stage, CancellationToken cancellationToken)
    {
        KeyRow? key = keys.Find(row => row.KeyId == stage.KeyId);
        if (key != null && key.State == SigningKeyStates.Introduced)
        {
            return AlreadyDone(stage.KeyId);
        }

        // A key ID is never reused, and only one key may wait to be activated at a time.
        KeyRow? otherIntroduced = keys.Find(row => row.State == SigningKeyStates.Introduced);
        if (key != null || otherIntroduced != null)
        {
            return NotAllowed(stage.KeyId, key?.State);
        }

        string? pem = await _vault.GetPrivateKeyPemAsync(stage.KeyId, cancellationToken);
        SigningKeyReading reading = SigningKeyMaterial.Read(pem);
        if (reading.PublicKeyPem is null)
        {
            return new RotateSigningKeyResult
            {
                Outcome = RotateSigningKeyOutcome.PrivateKeyUnusable,
                KeyId = stage.KeyId,
                Reason = reading.Reason,
            };
        }

        await InsertIntroducedAsync(writer, stage, reading.PublicKeyPem, cancellationToken);
        await RecordAuditAsync(writer, stage, "SigningKeyIntroduced", new { keyId = stage.KeyId }, cancellationToken);
        return Completed(stage.KeyId);
    }

    private async Task<RotateSigningKeyResult> ActivateAsync(
        IWriterConnection writer, List<KeyRow> keys, StageContext stage, CancellationToken cancellationToken)
    {
        KeyRow? key = keys.Find(row => row.KeyId == stage.KeyId);
        if (key != null && key.State == SigningKeyStates.Active)
        {
            return AlreadyDone(stage.KeyId);
        }

        if (key is null || key.State != SigningKeyStates.Introduced)
        {
            return NotAllowed(stage.KeyId, key?.State);
        }

        // With no active key no token exists yet, so no cache can be stale: the first key of a
        // fresh deployment may activate at once instead of leaving sign-in down for the wait.
        KeyRow? active = keys.Find(row => row.State == SigningKeyStates.Active);
        DateTimeOffset activationAllowedAt = AsUtc(key.IntroducedAt) + _settings.MinimumActivationWait;
        if (active != null && stage.Now < activationAllowedAt)
        {
            return new RotateSigningKeyResult
            {
                Outcome = RotateSigningKeyOutcome.NotYetPropagated,
                KeyId = stage.KeyId,
                AllowedAt = activationAllowedAt,
            };
        }

        await ActivateKeyAsync(writer, stage, active?.KeyId, cancellationToken);
        var details = new { keyId = stage.KeyId, previouslyActiveKeyId = active?.KeyId };
        await RecordAuditAsync(writer, stage, "SigningKeyActivated", details, cancellationToken);
        return Completed(stage.KeyId);
    }

    private async Task<RotateSigningKeyResult> RetireAsync(
        IWriterConnection writer, List<KeyRow> keys, StageContext stage, CancellationToken cancellationToken)
    {
        KeyRow? key = keys.Find(row => row.KeyId == stage.KeyId);
        if (key != null && key.State == SigningKeyStates.Retired)
        {
            return AlreadyDone(stage.KeyId);
        }

        if (key is null || key.State != SigningKeyStates.Previous)
        {
            return NotAllowed(stage.KeyId, key?.State);
        }

        DateTimeOffset retirementAllowedAt = AsUtc(key.SupersededAt!.Value) + _settings.AccessTokenLifetime;
        if (stage.Now < retirementAllowedAt)
        {
            return new RotateSigningKeyResult
            {
                Outcome = RotateSigningKeyOutcome.RetirementTooEarly,
                KeyId = stage.KeyId,
                AllowedAt = retirementAllowedAt,
            };
        }

        await RetireKeyAsync(writer, stage, cancellationToken);
        await RecordAuditAsync(writer, stage, "SigningKeyRetired", new { keyId = stage.KeyId }, cancellationToken);
        return Completed(stage.KeyId);
    }

    // timestamptz reads back as a UTC DateTime.
    private static DateTimeOffset AsUtc(DateTime utc)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static RotateSigningKeyResult Completed(string keyId)
    {
        return new RotateSigningKeyResult { Outcome = RotateSigningKeyOutcome.Completed, KeyId = keyId };
    }

    private static RotateSigningKeyResult AlreadyDone(string keyId)
    {
        return new RotateSigningKeyResult { Outcome = RotateSigningKeyOutcome.AlreadyDone, KeyId = keyId };
    }

    private static RotateSigningKeyResult NotAllowed(string keyId, string? currentState)
    {
        return new RotateSigningKeyResult
        {
            Outcome = RotateSigningKeyOutcome.StageNotAllowed,
            KeyId = keyId,
            CurrentState = currentState,
        };
    }

    private sealed class StageContext
    {
        public required string KeyId { get; init; }

        public required string PerformedBy { get; init; }

        public required string CorrelationId { get; init; }

        public required DateTimeOffset Now { get; init; }
    }


    private sealed class KeyRow
    {
        public string KeyId { get; init; } = string.Empty;

        public string State { get; init; } = string.Empty;

        public DateTime IntroducedAt { get; init; }

        public DateTime? SupersededAt { get; init; }
    }
}
