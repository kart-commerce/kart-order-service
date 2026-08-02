using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain.Orders;

namespace KartOrderService.Application.Common.Compensation;

/// <summary>
/// tickets.md's own note: "ORD-8 and ORD-13 are the same shape of task (compensating action
/// releasing Inventory, then a terminal write) and should share an implementation module." Reused
/// by ORD-5 (client cancel) and ORD-12's `cancel` action too — all four release paths are the
/// identical idempotent-per-line-item release, differing only in which terminal transition follows.
/// </summary>
public sealed class InventoryReleaseCompensator(IInventoryClient inventoryClient)
{
    /// <summary>Releases every line item that still carries a reservation id — idempotent per Inventory's own release contract, so calling this against an order some/all of whose reservations were already released elsewhere is always safe.</summary>
    public Task ReleaseAllAsync(Order order, CancellationToken cancellationToken) =>
        Task.WhenAll(order.LineItems
            .Where(li => li.ReservationId.HasValue)
            .Select(li => inventoryClient.ReleaseAsync(li.ReservationId!.Value, cancellationToken)));
}
