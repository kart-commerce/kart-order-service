namespace KartOrderService.Application.Common.Interfaces;

public enum InventoryReserveOutcome
{
    Reserved,
    InsufficientStock,
    Unavailable,
}

public sealed record InventoryReserveResult(InventoryReserveOutcome Outcome, Guid? ReservationId);

/// <summary>
/// architecture.md's one genuinely synchronous outbound edge (`POST /inventory/reserve`, ADR-0009)
/// plus its compensating counterpart (`POST /inventory/release`). Inventory's own approved contract
/// reserves exactly one `(sku, qty)` per call — never a whole order — hence `ReserveAsync` is
/// per-line-item (`contracts/README.md` addendum #1); `ReleaseAsync` is idempotent on
/// `reservationId` per Inventory's own contract, so callers never need their own "already released"
/// tracking.
/// </summary>
public interface IInventoryClient
{
    /// <summary>2s timeout + circuit breaker (design-decisions.md) applied by the registered `HttpClient`; a timeout/open-breaker surfaces as <see cref="InventoryReserveOutcome.Unavailable"/>, never a thrown exception the caller must guard against.</summary>
    Task<InventoryReserveResult> ReserveAsync(Guid orderId, string sku, int qty, CancellationToken cancellationToken);

    Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken);
}
