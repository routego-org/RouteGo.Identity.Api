namespace RouteGo.Identity.Api.Domain;

/// <summary>
/// How often a verification link may be resent for one email: at most 1 accepted resend per rolling
/// minute and 5 per rolling 24 hours. The link issued at registration or application does not
/// count, and refused requests never count.
/// </summary>
public static class ResendLimits
{
    private const int MaxPerDay = 5;

    private static readonly TimeSpan _minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _day = TimeSpan.FromHours(24);

    /// <summary>How long until another resend is allowed; zero when it is allowed now.</summary>
    /// <param name="acceptedResends">When each accepted resend of the last 24 hours was issued, oldest first.</param>
    /// <param name="now">The time of the new request.</param>
    public static TimeSpan GetRetryAfter(IReadOnlyList<DateTimeOffset> acceptedResends, DateTimeOffset now)
    {
        TimeSpan retryAfter = TimeSpan.Zero;
        if (acceptedResends.Count == 0)
        {
            return retryAfter;
        }

        DateTimeOffset latest = acceptedResends[acceptedResends.Count - 1];
        if (now - latest < _minute)
        {
            retryAfter = _minute - (now - latest);
        }

        if (acceptedResends.Count >= MaxPerDay)
        {
            // The oldest of the most recent five must age out of the 24-hour window first.
            DateTimeOffset oldestOfLastFive = acceptedResends[acceptedResends.Count - MaxPerDay];
            TimeSpan untilWindowFrees = oldestOfLastFive + _day - now;
            retryAfter = untilWindowFrees > retryAfter ? untilWindowFrees : retryAfter;
        }

        return retryAfter;
    }
}
