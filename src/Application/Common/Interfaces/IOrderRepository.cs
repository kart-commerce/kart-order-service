using KartOrderService.Domain.Orders;

namespace KartOrderService.Application.Common.Interfaces;

/// <summary>One repository per aggregate root — never a generic `IRepository&lt;T&gt;` (coding-standards.md).</summary>
public interface IOrderRepository
{
    /// <summary>Loads the full aggregate — line items and events included (Order's sequence numbering needs its full event history in memory; ORD-1..ORD-14's handlers all load the whole aggregate, never a partial projection).</summary>
    Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken);

    Task<Order?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>ORD-10: resolves `DeliveryStatusUpdated`'s `{trackingId, status}`-only payload back to the order that captured this `trackingId` from `ShipmentDispatched` (ORD-9) — `contracts/README.md` addendum #6.</summary>
    Task<Order?> GetByTrackingIdAsync(string trackingId, CancellationToken cancellationToken);

    void Add(Order order);

    /// <summary>ORD-14: orders currently in <paramref name="status"/> whose `updated_at` is older than <paramref name="olderThan"/> — the reconciliation sweep's stuck-saga candidates (`idx_orders_status_created`).</summary>
    Task<IReadOnlyList<Order>> GetStuckAsync(OrderStatus status, DateTimeOffset olderThan, CancellationToken cancellationToken);
}
