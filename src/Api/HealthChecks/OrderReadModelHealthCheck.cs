using KartOrderService.Infrastructure.Persistence.ReadModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KartOrderService.Api.HealthChecks;

/// <summary>Readiness signal for the Mongo read side — `GET /v1/orders/{id}` (ORD-4) is unusable if this is unreachable, even though PostgreSQL itself is healthy.</summary>
public sealed class OrderReadModelHealthCheck(OrderReadDbContext readDbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await readDbContext.Orders.EstimatedDocumentCountAsync(cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Order read-model MongoDB is unreachable", exception);
        }
    }
}
