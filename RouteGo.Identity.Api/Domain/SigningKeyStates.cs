namespace RouteGo.Identity.Api.Domain;

/// <summary>
/// Where a signing key is in its rotation. A key moves only Introduced → Active → Previous →
/// Retired; Introduced, Active and Previous keys are published, Retired keys never are.
/// </summary>
public static class SigningKeyStates
{
    /// <summary>Published, not yet signing.</summary>
    public const string Introduced = "Introduced";

    /// <summary>Published, and signs every new access token. Exactly one key is active.</summary>
    public const string Active = "Active";

    /// <summary>Published, no longer signs; tokens it signed stay valid until they expire.</summary>
    public const string Previous = "Previous";

    /// <summary>Removed from the public key set for good.</summary>
    public const string Retired = "Retired";

    /// <summary>Every state whose keys belong in the public key set.</summary>
    public static IReadOnlyList<string> Published { get; } = new[] { Introduced, Active, Previous };

    /// <summary>Whether a key in this state belongs in the public key set.</summary>
    /// <param name="state">The key's state.</param>
    /// <returns>True for Introduced, Active and Previous.</returns>
    public static bool IsPublished(string state)
    {
        return state == Introduced || state == Active || state == Previous;
    }
}
