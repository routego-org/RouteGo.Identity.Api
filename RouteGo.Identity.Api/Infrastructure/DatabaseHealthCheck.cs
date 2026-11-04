using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RouteGo.Identity.Api.Infrastructure;

/// <summary>
/// Readiness: Identity is ready only when it can reach its database. A not-ready instance must
/// receive no traffic, so a failure here answers unhealthy rather than throwing.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IReaderConnectionFactory _readerFactory;

    public DatabaseHealthCheck(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using DbConnection connection = _readerFactory.CreateReadConnection();
            await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return HealthCheckResult.Healthy();
        }
        catch (DbException exception)
        {
            return HealthCheckResult.Unhealthy("The database cannot be reached.", exception);
        }
    }
}
