using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace KartOrderService.Infrastructure.Persistence.ReadModel;

/// <summary>Declares Mongo indexes idempotently at startup, fire-and-forget — mirrors the manifest topology-declare pattern. `_id` is already the natural unique key (`orderId`), so no extra unique index is needed; this adds a supporting index for the one other query shape (`GET /v1/orders/{id}` is a plain `_id` lookup already covered by the default `_id` index).</summary>
public sealed class MongoIndexInitializerHostedService(OrderReadDbContext readDbContext, ILogger<MongoIndexInitializerHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var userIdIndex = new CreateIndexModel<Documents.OrderReadDocument>(
                Builders<Documents.OrderReadDocument>.IndexKeys.Ascending(d => d.UserId));
            await readDbContext.Orders.Indexes.CreateOneAsync(userIdIndex, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to declare Mongo indexes for order_read_model at startup; a Mongo outage at boot must never crash the host.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
