using RouteGo.Identity.Api.Domain;

namespace RouteGo.Identity.Api.Common.Sessions;

/// <summary>The account a session is issued to — exactly what its access token names.</summary>
/// <param name="UserId">The sub claim; for an operator also the operatorId claim.</param>
/// <param name="Email">The email claim.</param>
/// <param name="Role">The role claim.</param>
public sealed record SessionSubject(Guid UserId, string Email, AccountRole Role);
