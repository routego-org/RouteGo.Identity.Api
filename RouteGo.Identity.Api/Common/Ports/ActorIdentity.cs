namespace RouteGo.Identity.Api.Common.Ports;

/// <summary>
/// Who a write is attributed to when no signed-in principal exists: the account a registration,
/// sign-in or verification acts on, or the system for background and command-line work.
/// </summary>
/// <param name="Id">The actor id, at most 64 characters.</param>
/// <param name="Email">The actor's email.</param>
/// <param name="Name">The actor's display name; accounts use their email, as signed-in actors do.</param>
public sealed record ActorIdentity(string Id, string Email, string Name)
{
    /// <summary>
    /// The reserved system actor for background and command-line work — the same values as the
    /// database layer's SystemActor.Default, so every system write reads the same in the audit log.
    /// </summary>
    public static ActorIdentity System { get; } =
        new ActorIdentity("00000000-0000-0000-0000-000000000001", "system@internal", "System");
}
