using Microsoft.Extensions.Logging;

namespace RouteGo.Identity.Api.Tests.Support;

/// <summary>Captures every log line the host writes, for asserting on security logging.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly CapturingLogger _logger = new CapturingLogger();

    /// <summary>Every captured line, in order.</summary>
    public IReadOnlyList<CapturedLog> Entries
    {
        get
        {
            return _logger.Entries;
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _logger;
    }

    public void Dispose()
    {
    }
}
