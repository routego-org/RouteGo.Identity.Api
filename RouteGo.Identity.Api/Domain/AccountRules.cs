using System.Text.RegularExpressions;

namespace RouteGo.Identity.Api.Domain;

/// <summary>
/// The authentication area's shared account rules: the password policy, the email length, and the
/// full-name and phone rules. Every password Identity accepts and every name and phone it stores
/// goes through these, so registration, operator application and admin creation agree.
/// </summary>
public static partial class AccountRules
{
    /// <summary>The longest email address an account may have.</summary>
    public const int MaxEmailLength = 254;

    /// <summary>The shortest full name.</summary>
    public const int MinFullNameLength = 2;

    /// <summary>The longest full name.</summary>
    public const int MaxFullNameLength = 100;

    private const int MinPasswordLength = 10;
    private const string IndiaPrefix = "+91";

    /// <summary>At least 10 characters, containing at least one letter and at least one digit.</summary>
    public static bool IsAcceptablePassword(string? password)
    {
        if (password is null || password.Length < MinPasswordLength)
        {
            return false;
        }

        return password.Any(char.IsAsciiLetter) && password.Any(char.IsAsciiDigit);
    }

    /// <summary>2–100 characters.</summary>
    public static bool IsAcceptableFullName(string? fullName)
    {
        if (fullName is null)
        {
            return false;
        }

        // Counted as the database counts them: an emoji is one character, not two UTF-16 units.
        int length = fullName.EnumerateRunes().Count();
        return length >= MinFullNameLength && length <= MaxFullNameLength;
    }

    /// <summary>An Indian mobile number: 10 digits starting 6–9, optionally prefixed +91.</summary>
    public static bool IsAcceptablePhone(string? phone)
    {
        return phone != null && IndianMobile().IsMatch(phone);
    }

    /// <summary>The phone as it is kept: +91 followed by the 10 digits; null when none was given.</summary>
    /// <param name="phone">A phone that passed <see cref="IsAcceptablePhone"/>, or null.</param>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone))
        {
            return null;
        }

        if (phone.StartsWith(IndiaPrefix, StringComparison.Ordinal))
        {
            return phone;
        }

        return IndiaPrefix + phone;
    }

    // \z, not $: in .NET a $ also matches before a trailing newline, which the database would refuse.
    [GeneratedRegex(@"^(\+91)?[6-9][0-9]{9}\z")]
    private static partial Regex IndianMobile();
}
