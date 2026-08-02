using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace KartOrderService.Infrastructure.Persistence;

/// <summary>
/// EF Core's owned-entity types (`OrderLineItem`/`OrderEvent`) are always eagerly loaded with
/// their owner — no explicit `.Include()` is needed for either, unlike a normal navigation.
/// </summary>
public sealed class OrderRepository(OrderDbContext dbContext) : IOrderRepository
{
    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken) =>
        dbContext.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

    public Task<Order?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        dbContext.Orders.FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<Order?> GetByTrackingIdAsync(string trackingId, CancellationToken cancellationToken) =>
        dbContext.Orders.FirstOrDefaultAsync(o => o.TrackingId == trackingId, cancellationToken);

    public void Add(Order order) => dbContext.Orders.Add(order);

    /// <summary>ORD-14: mirrors database-design.md's own named sweep query — `WHERE status = $awaited_status AND created_at < now() - $threshold` (`idx_orders_status_created`).</summary>
    public async Task<IReadOnlyList<Order>> GetStuckAsync(OrderStatus status, DateTimeOffset olderThan, CancellationToken cancellationToken) =>
        await dbContext.Orders
            .Where(o => o.Status == status && o.CreatedAt < olderThan)
            .ToListAsync(cancellationToken);
}
