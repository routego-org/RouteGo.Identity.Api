namespace RouteGo.Identity.Api.Common.Accounts;

/// <summary>Where verification links point and how long they work.</summary>
/// <param name="PortalBaseUrl">The Web Portal's public address, without a trailing slash.</param>
/// <param name="Lifetime">How long a link works after it is issued (default 24 hours).</param>
public sealed record VerificationLinkSettings(string PortalBaseUrl, TimeSpan Lifetime);
