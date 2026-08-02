using KartOrderService.Infrastructure.Persistence;
using KartOrderService.Infrastructure.Persistence.ReadModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Infrastructure.Messaging;

/// <summary>
/// ORD-3: a direct-Postgres poller, deliberately NOT a RabbitMQ self-consumer (contrast with
/// `kart-payment-service`'s CQRS projector — see `contracts/README.md`). database-design.md's own
/// Read Model section: the projector needs every `order_events` transition row, not just the
/// subset with a published `event_type`, so it reads `order_events` directly via its own
/// `projected_at` progress marker (addendum #2), independent of the Outbox poller's `published_at`.
/// </summary>
public sealed class OrderReadModelProjectorHostedService(IServiceScopeFactory scopeFactory, ILogger<OrderReadModelProjectorHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProjectPendingBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Order read-model projector failed to process a batch; retrying after the normal poll interval.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProjectPendingBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var writer = scope.ServiceProvider.GetRequiredService<ReadModelProjectionWriter>();

        // EF Core cannot track a bare owned-entity projection (SelectMany straight to OrderEvent)
        // without its owner present in the result set - load the owning Orders that have at least
        // one unprojected event, then work with their Events collection in memory instead.
        var pendingOrders = await dbContext.Orders
            .Where(o => o.Events.Any(e => e.ProjectedAt == null))
            .OrderBy(o => o.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pendingOrders.Count == 0)
        {
            return;
        }

        var pending = pendingOrders
            .SelectMany(o => o.Events)
            .Where(e => e.ProjectedAt == null)
            .OrderBy(e => e.CreatedAt)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        foreach (var orderEvent in pending)
        {
            await writer.ApplyAsync(orderEvent, cancellationToken);
            orderEvent.MarkProjected(now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
