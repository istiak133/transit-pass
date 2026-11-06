using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace TransitPass.BuildingBlocks.Hosting;

/// <summary>
/// Readiness check: borrows a connection from the pool and runs <c>SELECT 1</c>.
/// If the database is down, the service reports "not ready" and the orchestrator stops sending it traffic,
/// instead of every request failing with a 500.
/// </summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", ex);
        }
    }
}
