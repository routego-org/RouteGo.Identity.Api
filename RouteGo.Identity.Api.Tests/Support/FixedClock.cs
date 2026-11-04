using RouteGo.Identity.Api.Common.Ports;

namespace RouteGo.Identity.Api.Tests.Support;

/// <summary>A clock that reads whatever time the test sets.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; set; }
}
