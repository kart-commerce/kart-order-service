namespace KartOrderService.Domain.Orders;

/// <summary>
/// ddd-model.md's `OrderLineItem` child entity — one per `(sku, qty, unitPrice)` line, immutable
/// after creation except for the two addendum reservation-tracking fields (see
/// `contracts/README.md` addendum #1: Inventory's real `POST /inventory/reserve` contract reserves
/// one `(sku, qty)` per call, not a whole order, so each line tracks its own reservation for later
/// release).
/// </summary>
public sealed class OrderLineItem
{
    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int Qty { get; private set; }

    public decimal UnitPrice { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>Addendum: Inventory's reservationId for this specific line, set once the synchronous reserve call for it succeeds.</summary>
    public Guid? ReservationId { get; private set; }

    /// <summary>Addendum: set once ORD-6's `InventoryReserved` consumer confirms this specific line's reservation.</summary>
    public DateTimeOffset? ReservationConfirmedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public string UpdatedBy { get; private set; } = string.Empty;

    /// <summary>EF Core materialization only.</summary>
    private OrderLineItem()
    {
    }

    internal OrderLineItem(Guid id, Guid orderId, string sku, int qty, decimal unitPrice, string currency, Guid? reservationId, string actingPrincipal, DateTimeOffset now)
    {
        Id = id;
        OrderId = orderId;
        Sku = sku;
        Qty = qty;
        UnitPrice = unitPrice;
        Currency = currency;
        ReservationId = reservationId;
        CreatedAt = now;
        UpdatedAt = now;
        CreatedBy = actingPrincipal;
        UpdatedBy = actingPrincipal;
    }

    /// <summary>Idempotent — a redelivered confirmation for an already-confirmed line is a no-op.</summary>
    internal void ConfirmReservation(string actingPrincipal, DateTimeOffset now)
    {
        if (ReservationConfirmedAt is not null)
        {
            return;
        }

        ReservationConfirmedAt = now;
        UpdatedAt = now;
        UpdatedBy = actingPrincipal;
    }
}
