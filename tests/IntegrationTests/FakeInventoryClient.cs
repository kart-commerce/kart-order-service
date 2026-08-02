using System.Collections.Concurrent;
using KartOrderService.Application.Common.Interfaces;

namespace KartOrderService.IntegrationTests;

/// <summary>
/// Test double for the one genuinely synchronous outbound edge (ADR-0009) - no real
/// kart-inventory-service is available to these integration tests, mirroring
/// `kart-payment-service`'s in-process `SimulatedPaymentGatewayAdapter` precedent for the same
/// reason. A SKU ending in <c>-OUT-OF-STOCK</c> deterministically simulates `InventoryReservationFailed`.
/// </summary>
public sealed class FakeInventoryClient : IInventoryClient
{
    public ConcurrentBag<Guid> ReleasedReservationIds { get; } = [];

    public Task<InventoryReserveResult> ReserveAsync(Guid orderId, string sku, int qty, CancellationToken cancellationToken)
    {
        if (sku.EndsWith("-OUT-OF-STOCK", StringComparison.Ordinal))
        {
            return Task.FromResult(new InventoryReserveResult(InventoryReserveOutcome.InsufficientStock, null));
        }

        return Task.FromResult(new InventoryReserveResult(InventoryReserveOutcome.Reserved, Guid.NewGuid()));
    }

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        ReleasedReservationIds.Add(reservationId);
        return Task.CompletedTask;
    }
}
