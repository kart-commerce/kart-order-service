using System.Text;
using Kart.Shared.Messaging;
using Kart.Shared.Observability;
using KartOrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace KartOrderService.Infrastructure.Messaging;

/// <summary>
/// ORD-2: relays `order_events` rows (`event_type IS NOT NULL AND published_at IS NULL` —
/// `idx_outbox_unpublished`) to whichever exchange/routing key `contracts/message-bus-manifest.json`'s
/// `publishedEvents` maps each event type to, in `created_at` order (edge-cases.md's "Outbox Publish
/// Failure/Reordering After DB Commit" — each `OrderEvent`'s own monotonic `Sequence` is already
/// embedded in its `Payload` from `Order.Create`/`Transition`, giving consumers a reordering guard).
/// Declares the full manifest topology idempotently on every (re)connect. Retries indefinitely until
/// RabbitMQ is reachable, rather than dead-lettering — the publish-side half of at-least-once
/// delivery. Mirrors `kart-payment-service`'s identically-shaped relay.
/// </summary>
public sealed class OutboxRelayHostedService(
    IServiceScopeFactory scopeFactory,
    IConnectionFactory connectionFactory,
    MessageBusManifest manifest,
    ILogger<OutboxRelayHostedService> logger) : BackgroundService
{
    private const string FlowName = "OrderManagementAdmin";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = connectionFactory.CreateConnection();
                using var channel = connection.CreateModel();
                RabbitMqTopologyProvisioner.Declare(channel, manifest);

                await RunRelayLoopAsync(channel, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Order outbox relay lost its RabbitMQ connection; reconnecting in {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task RunRelayLoopAsync(IModel channel, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RelayPendingBatchAsync(channel, stoppingToken);
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RelayPendingBatchAsync(IModel channel, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        // EF Core cannot track a bare owned-entity projection (SelectMany straight to OrderEvent)
        // without its owner present in the result set - load the owning Orders that have at least
        // one qualifying event, then work with their Events collection in memory instead.
        var pendingOrders = await dbContext.Orders
            .Where(o => o.Events.Any(e => e.EventType != null && e.PublishedAt == null))
            .OrderBy(o => o.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pendingOrders.Count == 0)
        {
            return;
        }

        // Same tie-break as OrderReadModelProjectorHostedService's own fix — two of one order's own
        // events can share an identical CreatedAt; Sequence (this aggregate's explicit monotonic
        // ordering) is the correct deterministic tiebreak, not EF Core's unspecified owned-
        // collection load order. Consumers already have Sequence embedded in the payload as a
        // reordering guard, but publishing in the right order to begin with costs nothing.
        var pending = pendingOrders
            .SelectMany(o => o.Events)
            .Where(e => e.EventType != null && e.PublishedAt == null)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Sequence)
            .ToList();

        using var _ = KartFlowContext.Push(FlowName);

        var now = DateTimeOffset.UtcNow;
        foreach (var outboxEvent in pending)
        {
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.MessageId = outboxEvent.Id.ToString();
            properties.ContentType = "application/json";

            var exchange = manifest.ExchangeFor(outboxEvent.EventType!);
            var routingKey = manifest.RoutingKeyFor(outboxEvent.EventType!);

            // `using var` (NOT an explicit `using (...) { }` block) so the publish Activity stays
            // current through the Stage log line below too — an explicit block would dispose it
            // before that log ran, silently leaving OutboxEventPublished untagged with any TraceId
            // (a real bug found+fixed in two sibling services). The stored TraceParent replays the
            // *originating* request's trace across this async hop, since Activity.Current here is
            // just the background poller's own unrelated activity.
            using var activity = RabbitMqTraceContext.StartPublishActivityFromStoredTraceParent(exchange, routingKey, outboxEvent.TraceParent, properties);

            channel.BasicPublish(
                exchange: exchange,
                routingKey: routingKey,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(outboxEvent.Payload ?? "{}"));

            outboxEvent.MarkPublished(now);

            logger.LogInformation(
                "Stage {Stage}: {EventType} outbox event {OutboxId} published to {Exchange}/{RoutingKey}",
                "OutboxEventPublished",
                outboxEvent.EventType,
                outboxEvent.Id,
                exchange,
                routingKey);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
