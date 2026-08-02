using KartOrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KartOrderService.Api.HealthChecks;

/// <summary>
/// Readiness signal for `/health/ready` — a database that is reachable but behind on migrations
/// must fail readiness too, not just an unreachable one, so a pod never accepts traffic while its
/// background workers (Outbox poller, read-model projector, reconciliation sweep) are looping on
/// errors against a schema they don't expect.
/// </summary>
public sealed class OrderDbHealthCheck(OrderDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

            return pending.Length == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{pending.Length} pending migration(s): {string.Join(", ", pending)}");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Order database is unreachable", exception);
        }
    }
}
