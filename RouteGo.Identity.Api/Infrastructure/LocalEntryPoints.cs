namespace RouteGo.Identity.Api.Infrastructure;

/// <summary>
/// The one place a developer sets what their machine points at — the local entry points for this
/// app's outbound configuration. Kept in C# rather than an appsettings.{Environment}.json so there
/// is a single list to read and no second file in which a key can be set here but forgotten there.
/// The values are seeded into the process environment block, so local runs and the deployed app
/// read configuration through the one identical channel (references/configuration.md).
/// Never put a real secret here — it would ship inside the assembly. Secrets stay in user-secrets
/// locally and Secrets Manager deployed, through SecretResolver (references/secrets.md).
/// </summary>
public static class LocalEntryPoints
{
    // The literal names, because this runs before there is an IHostEnvironment to ask: the
    // framework never exposes them as constants (WebHostDefaults.EnvironmentKey is the config key,
    // not the variable). The web stacks prefer ASPNETCORE_ENVIRONMENT; the generic host the lambda
    // worker builds reads only DOTNET_ENVIRONMENT — checking both mirrors the framework's own
    // resolution, so the guard behaves identically on every stack.
    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";
    private const string DotNetEnvironmentVariable = "DOTNET_ENVIRONMENT";

    private const string AwsRegion = "ap-south-1";

    /// <summary>
    /// Seeds the local values when this process runs as Development, and does nothing otherwise.
    /// MUST be called before the host builder is created: the builder's environment-variable
    /// provider snapshots the process block once, so a value set after that never reaches
    /// configuration.
    /// </summary>
    public static void Apply()
    {
        string? environmentName = Environment.GetEnvironmentVariable(AspNetCoreEnvironmentVariable)
            ?? Environment.GetEnvironmentVariable(DotNetEnvironmentVariable);
        if (environmentName != Environments.Development)
        {
            return;
        }

        Environment.SetEnvironmentVariable(ConfigKeys.AwsRegion, AwsRegion);

        // ══════════════════════════════════════════════════════════════════════
        // FILLED BY the sibling slice skill and by feature work: one line per key
        // in ConfigKeys, with the value this developer's machine should use — all of
        // it inside this seam (a field declared above it fails the foundation audit).
        //
        //   Environment.SetEnvironmentVariable(ConfigKeys.BackendBaseUrl, "http://localhost:5088/");
        //
        // A base URL carrying a path MUST end in '/' — see RequireBaseAddress in
        // ConfigurationValidation for what happens when it does not.
        // ══════════════════════════════════════════════════════════════════════
        // A local run is one always-on process serving concurrent requests — the ManagedInstances shape; a
        // developer's own traffic fans out to a handful of parallel calls, and 10 connections covers that.
        Environment.SetEnvironmentVariable(ConfigKeys.DbConnectionProfile, "ManagedInstances");
        Environment.SetEnvironmentVariable(ConfigKeys.DbMaxPoolSize, "10");
        // The spec's defaults (architecture/service-context.md).
        Environment.SetEnvironmentVariable(ConfigKeys.AccessTokenMinutes, "15");
        Environment.SetEnvironmentVariable(ConfigKeys.SigningKeyActivationWaitMinutes, "10");
        Environment.SetEnvironmentVariable(ConfigKeys.VerificationTokenHours, "24");
        Environment.SetEnvironmentVariable(ConfigKeys.RefreshTokenDays, "7");
        Environment.SetEnvironmentVariable(ConfigKeys.Issuer, "http://localhost:5001");
        Environment.SetEnvironmentVariable(ConfigKeys.Audience, "routego");
        // The Web Portal's local address (00-overall-architecture: port 8080).
        Environment.SetEnvironmentVariable(ConfigKeys.PortalBaseUrl, "http://localhost:8080");
        // LocalStack from docker-compose.yml; its account id is always 000000000000.
        Environment.SetEnvironmentVariable(ConfigKeys.AwsServiceUrl, "http://localhost:4566");
        Environment.SetEnvironmentVariable(
            ConfigKeys.IdentityEventsTopicArn, "arn:aws:sns:ap-south-1:000000000000:routego-identity-events");
        Environment.SetEnvironmentVariable(
            ConfigKeys.AuditEventsTopicArn, "arn:aws:sns:ap-south-1:000000000000:routego-audit-events");
    }
}
