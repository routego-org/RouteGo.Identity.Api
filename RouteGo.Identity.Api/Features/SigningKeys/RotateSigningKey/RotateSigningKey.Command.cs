namespace RouteGo.Identity.Api.Features.SigningKeys.RotateSigningKey;

/// <summary>
/// The command-line entry point a platform engineer runs one stage with:
/// <c>rotate-key --stage introduce|activate|retire --key-id &lt;key ID&gt; --by &lt;name&gt;</c>.
/// It prints the outcome and exits; it never starts the web server or the event publisher.
/// </summary>
public sealed class RotateSigningKeyCommand
{
    /// <summary>The first argument that selects this command.</summary>
    public const string Name = "rotate-key";

    /// <summary>The stage ran, or had already run (nothing changed).</summary>
    public const int ExitSucceeded = 0;

    /// <summary>The stage was refused; nothing changed.</summary>
    public const int ExitRefused = 2;

    /// <summary>The command line was not understood.</summary>
    public const int ExitUsage = 64;

    private const string Usage = "usage: rotate-key --stage introduce|activate|retire --key-id <key ID> --by <your name>";

    private readonly RotateSigningKeyHandler _handler;
    private readonly RotateSigningKeyValidator _validator = new RotateSigningKeyValidator();

    public RotateSigningKeyCommand(
        IActorWriterConnectionFactory writerFactory, ISigningKeyVault vault, IClock clock, SigningKeyRotationSettings settings)
    {
        _handler = new RotateSigningKeyHandler(writerFactory, vault, clock, settings);
    }

    /// <summary>Whether the process was started to run this command.</summary>
    /// <param name="args">The process arguments.</param>
    public static bool IsInvoked(string[] args)
    {
        return args.Length > 0 && args[0] == Name;
    }

    /// <summary>Parses the arguments, runs the stage, and prints the outcome.</summary>
    /// <param name="args">The process arguments, starting with <see cref="Name"/>.</param>
    /// <param name="output">Where the outcome is printed.</param>
    /// <param name="cancellationToken">Stops the stage before it commits.</param>
    /// <returns>The process exit code.</returns>
    public async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        RotateSigningKeyRequest? request = ParseArguments(args);
        if (request is null)
        {
            await output.WriteLineAsync(Usage);
            return ExitUsage;
        }

        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            await output.WriteLineAsync(string.Join(Environment.NewLine, validation.Errors));
            await output.WriteLineAsync(Usage);
            return ExitUsage;
        }

        RotateSigningKeyResult result = await _handler.HandleAsync(request, Guid.NewGuid().ToString(), cancellationToken);
        await output.WriteLineAsync(Describe(request.Stage!, result));
        if (result.Outcome is RotateSigningKeyOutcome.Completed or RotateSigningKeyOutcome.AlreadyDone)
        {
            return ExitSucceeded;
        }

        return ExitRefused;
    }

    private static RotateSigningKeyRequest? ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int index = 1; index < args.Length; index += 2)
        {
            bool hasValue = index + 1 < args.Length;
            if (!hasValue || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            options[args[index]] = args[index + 1];
        }

        options.TryGetValue("--stage", out string? stage);
        options.TryGetValue("--key-id", out string? keyId);
        options.TryGetValue("--by", out string? performedBy);
        return new RotateSigningKeyRequest(stage, keyId, performedBy);
    }

    // The codes are the cataloged worker codes; the engineer reads them, no HTTP client does.
    private static string Describe(string stage, RotateSigningKeyResult result)
    {
        string currentState = result.CurrentState ?? "none (no such key)";
        return result.Outcome switch
        {
            RotateSigningKeyOutcome.Completed => $"{stage} completed for key {result.KeyId}.",
            RotateSigningKeyOutcome.AlreadyDone => $"Nothing to do: {stage} has already completed for key {result.KeyId}.",
            RotateSigningKeyOutcome.NotYetPropagated =>
                $"SIGNING_KEY_NOT_YET_PROPAGATED: key {result.KeyId} activationAllowedAt={result.AllowedAt:O}",
            RotateSigningKeyOutcome.RetirementTooEarly =>
                $"SIGNING_KEY_RETIREMENT_TOO_EARLY: key {result.KeyId} retirementAllowedAt={result.AllowedAt:O}",
            RotateSigningKeyOutcome.StageNotAllowed =>
                $"SIGNING_KEY_STAGE_NOT_ALLOWED: key {result.KeyId} currentState={currentState}",
            RotateSigningKeyOutcome.PrivateKeyUnusable =>
                $"SIGNING_KEY_PRIVATE_HALF_UNUSABLE: key {result.KeyId} reason={result.Reason}",
            _ => throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'."),
        };
    }
}
