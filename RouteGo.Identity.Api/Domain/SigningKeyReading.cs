namespace RouteGo.Identity.Api.Domain;

/// <summary>What reading a stored signing key found: its public half, or why it cannot sign.</summary>
/// <param name="PublicKeyPem">The public half in PEM form when the key can sign; otherwise null.</param>
/// <param name="Reason">Why the key cannot sign, when it cannot; otherwise null.</param>
public sealed record SigningKeyReading(string? PublicKeyPem, string? Reason);
