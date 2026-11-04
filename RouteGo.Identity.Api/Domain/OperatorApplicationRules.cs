using System.Text.RegularExpressions;

namespace RouteGo.Identity.Api.Domain;

/// <summary>
/// The operator-onboarding area's shared rules: company details, the rejection reason, and the
/// wait before a rejected operator may re-apply. Applying, re-applying and review all use these.
/// </summary>
public static partial class OperatorApplicationRules
{
    /// <summary>The longest contact email an application may carry.</summary>
    public const int MaxContactEmailLength = 254;

    private const int MinCompanyNameLength = 2;
    private const int MaxCompanyNameLength = 150;
    private const int MinRejectReasonLength = 10;
    private const int MaxRejectReasonLength = 500;

    /// <summary>How long after a rejection the operator must wait before re-applying.</summary>
    public static readonly TimeSpan ReapplicationWait = TimeSpan.FromDays(7);

    /// <summary>2–150 characters, counted as the database counts them.</summary>
    public static bool IsAcceptableCompanyName(string? companyName)
    {
        if (companyName is null)
        {
            return false;
        }

        int length = companyName.EnumerateRunes().Count();
        return length >= MinCompanyNameLength && length <= MaxCompanyNameLength;
    }

    /// <summary>
    /// A 15-character GSTIN in the standard format, upper case: 2 digits, 5 letters, 4 digits,
    /// 1 letter, 1 letter or digit 1–9, the letter Z, 1 letter or digit.
    /// </summary>
    public static bool IsAcceptableGstin(string? gstin)
    {
        return gstin != null && Gstin().IsMatch(gstin);
    }

    /// <summary>A rejection reason of 10–500 characters, counted as the database counts them.</summary>
    public static bool IsAcceptableRejectReason(string? reason)
    {
        if (reason is null)
        {
            return false;
        }

        int length = reason.EnumerateRunes().Count();
        return length >= MinRejectReasonLength && length <= MaxRejectReasonLength;
    }

    /// <summary>The earliest time a rejected operator may re-apply.</summary>
    /// <param name="rejectedAt">When the operator's latest application was rejected.</param>
    public static DateTimeOffset ReapplyAllowedAt(DateTimeOffset rejectedAt)
    {
        return rejectedAt + ReapplicationWait;
    }

    // \z, not $: in .NET a $ also matches before a trailing newline, which the database would refuse.
    [GeneratedRegex(@"^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]\z")]
    private static partial Regex Gstin();
}
