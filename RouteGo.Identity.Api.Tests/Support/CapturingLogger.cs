using Microsoft.Extensions.Logging;

namespace RouteGo.Identity.Api.Tests.Support;

/// <summary>One captured log line.</summary>
/// <param name="Level">Its level.</param>
/// <param name="Message">The rendered message.</param>
public sealed record CapturedLog(LogLevel Level, string Message);

/// <summary>Keeps every log line so a test can assert on alerts and security logging.</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly List<CapturedLog> _entries = new List<CapturedLog>();

    /// <summary>Every captured line, in order.</summary>
    public IReadOnlyList<CapturedLog> Entries
    {
        get
        {
            return _entries;
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _entries.Add(new CapturedLog(logLevel, formatter(state, exception)));
    }
}
