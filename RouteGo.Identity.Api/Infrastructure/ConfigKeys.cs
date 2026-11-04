namespace RouteGo.Identity.Api.Infrastructure;

/// <summary>
/// The name of every environment-supplied configuration value, spelled exactly once. The names are
/// deliberately FLAT: each maps 1:1 to an environment variable of the same spelling, so no ':' or
/// '__' separator can be mistyped between the deployment template, the pipeline, and the reader
/// here. LocalEntryPoints and every consumer go through these constants, so a rename is a compile
/// error rather than a value that silently arrives null (references/configuration.md).
/// </summary>
public static class ConfigKeys
{
    /// <summary>The AWS SDK's own region variable, and the ONLY region source the app reads.
    /// Lambda sets it; LocalEntryPoints seeds it on a laptop. There is deliberately no app-owned
    /// AWS:Region key that could disagree with the runtime's own value.</summary>
    public const string AwsRegion = "AWS_REGION";

    // ══════════════════════════════════════════════════════════════════════════
    // FILLED BY the sibling slice skill and by feature work: one const per value
    // that varies by environment — a base URL, a client id, a public origin.
    //
    //   public const string BackendBaseUrl = "BackendBaseUrl";
    //
    // Adding one is four more edits, none of them a new file: a local value in
    // LocalEntryPoints, a parameter + env var in the deployment template, a
    // pipeline variable, and a RequireKeys entry if the app cannot start without
    // it. NEVER add a configuration key to an appsettings*.json.
    //
    // Otel:Exporter:* and AWS:Profile do NOT belong here — see
    // references/configuration.md for why those two keep their nested names.
    // ══════════════════════════════════════════════════════════════════════════
    /// <summary>Which Lambda execution model this process runs on — Lambda (one invocation per process,
    /// frozen between them) or ManagedInstances (many concurrent invocations in one always-on process).
    /// It picks the database pool profile (Infrastructure/Pgsql/ConnectionProfile). Set by the deployment
    /// template, never sniffed from the runtime. Required.</summary>
    public const string DbConnectionProfile = "DB_CONNECTION_PROFILE";

    /// <summary>The most database connections one process may hold: 2-4 on Lambda (a handler's opens are
    /// sequential), at least the per-environment concurrency on ManagedInstances. Set by the deployment
    /// template beside DbConnectionProfile. Required.</summary>
    public const string DbMaxPoolSize = "DB_MAX_POOL_SIZE";

    /// <summary>ARN of the SNS topic UserRegistered, OperatorApproved and OperatorRejected are
    /// published to. Deployed from SSM /routego/prod/identity/identity-events-topic-arn. Required.</summary>
    public const string IdentityEventsTopicArn = "IdentityEventsTopicArn";

    /// <summary>ARN of the SNS topic AuditRecorded is published to. Deployed from SSM
    /// /routego/prod/identity/audit-events-topic-arn. Required.</summary>
    public const string AuditEventsTopicArn = "AuditEventsTopicArn";

    /// <summary>Access-token lifetime in minutes (default 15). Deployed from SSM
    /// /routego/prod/identity/access-token-minutes. Required.</summary>
    public const string AccessTokenMinutes = "AccessTokenMinutes";

    /// <summary>Minimum minutes between introducing and activating a signing key (default 10) — the
    /// longest a consuming service may cache the public key set. Deployed from SSM
    /// /routego/prod/identity/signing-key-activation-wait-minutes. Required.</summary>
    public const string SigningKeyActivationWaitMinutes = "SigningKeyActivationWaitMinutes";

    /// <summary>The Web Portal's public address; verification links point at its /auth/verify page.
    /// Deployed from SSM /routego/prod/identity/portal-base-url. Required.</summary>
    public const string PortalBaseUrl = "PortalBaseUrl";

    /// <summary>Verification-link lifetime in hours (default 24). Deployed from SSM
    /// /routego/prod/identity/verification-token-hours. Required.</summary>
    public const string VerificationTokenHours = "VerificationTokenHours";

    /// <summary>The iss claim of every access token — the Portal's public address. Deployed from SSM
    /// /routego/prod/identity/issuer. Required.</summary>
    public const string Issuer = "Issuer";

    /// <summary>The aud claim of every access token (routego). Deployed from SSM
    /// /routego/prod/identity/audience. Required.</summary>
    public const string Audience = "Audience";

    /// <summary>Refresh-token lifetime in days (default 7). Deployed from SSM
    /// /routego/prod/identity/refresh-token-days. Required.</summary>
    public const string RefreshTokenDays = "RefreshTokenDays";

    /// <summary>An AWS endpoint override — LocalStack on a developer's machine. Unset when deployed,
    /// where the SDK uses the real AWS endpoints and the instance role.</summary>
    public const string AwsServiceUrl = "AwsServiceUrl";
}
