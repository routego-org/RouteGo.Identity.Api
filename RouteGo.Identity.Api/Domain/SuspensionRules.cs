namespace RouteGo.Identity.Api.Domain;

/// <summary>
/// The rules suspension and reactivation share: which accounts may be suspended, and the reason
/// an admin must give for either action.
/// </summary>
public static class SuspensionRules
{
    private const int MinReasonLength = 10;
    private const int MaxReasonLength = 500;

    /// <summary>A reason of 10–500 characters, counted as the database counts them.</summary>
    public static bool IsAcceptableReason(string? reason)
    {
        if (reason is null)
        {
            return false;
        }

        int length = reason.EnumerateRunes().Count();
        return length >= MinReasonLength && length <= MaxReasonLength;
    }

    /// <summary>Admin accounts are never suspended; customers and operators may be.</summary>
    public static bool CanBeSuspended(AccountRole role)
    {
        return role != AccountRole.Admin;
    }
}
