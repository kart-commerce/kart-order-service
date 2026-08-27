using Kart.Shared.Observability;
using KartOrderService.Domain.Orders;
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
    private const string FlowName = "OrderManagementAdmin";
    private const string ShoppingJourneyFlowName = "NormalShoppingPurchaseJourney";
    private static readonly HashSet<string> ShoppingJourneyEventTypes = ["OrderCreated", "OrderConfirmed"];

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

        // Order Management (Admin) flow #7's real end-to-end run found this: when two events for
        // the *same* order share an identical CreatedAt (a real possibility whenever a saga step
        // advances a status immediately after the prior one — confirmed via a bulk-seeding tool
        // that captures one `now` across several of an order's own transitions, but not
        // structurally impossible for genuinely fast real transitions either), sorting by
        // CreatedAt alone leaves their relative order to EF Core's owned-collection load, which is
        // never guaranteed to match insertion/Sequence order. A later event applying its `$set`
        // upsert before OrderCreated's own `SetOnInsert` upsert silently drops every SetOnInsert
        // field (UserId/Items/TotalAmount/CreatedAt) for good, since SetOnInsert is a no-op once
        // the document already exists. Sequence is this aggregate's own explicit, monotonic
        // per-order ordering — ThenBy resolves the tie deterministically without changing anything
        // when timestamps already differ (the common case).
        var pending = pendingOrders
            .SelectMany(o => o.Events)
            .Where(e => e.ProjectedAt == null)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Sequence)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        foreach (var orderEvent in pending)
        {
            // Named published events map to the flow that produced them (OrderCreated/OrderConfirmed
            // -> the shopping journey, every other named event -> Order Management Admin, per this
            // service's event-contract.md); an internal-only transition (EventType null — e.g.
            // Created->Reserved, Paid->Shipped) gets no Flow tag rather than a guessed one.
            var flow = orderEvent.EventType switch
            {
                null => (string?)null,
                var t when ShoppingJourneyEventTypes.Contains(t) => ShoppingJourneyFlowName,
                _ => FlowName,
            };
            using var flowScope = flow is null ? null : KartFlowContext.Push(flow);

            await writer.ApplyAsync(orderEvent, cancellationToken);
            orderEvent.MarkProjected(now);

            logger.LogInformation(
                "Stage {Stage}: read-model persisted for order {OrderId} ({ToStatus}, event {EventType})",
                "OrderReadModelPersisted",
                orderEvent.OrderId,
                orderEvent.ToStatus,
                orderEvent.EventType ?? "(internal)");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
