namespace KartOrderService.Domain.Orders;

/// <summary>ddd-model.md's `OrderStatus` value object — the 8-value lifecycle/Saga state.</summary>
public enum OrderStatus
{
    Created,
    Reserved,
    Paid,
    Shipped,
    Delivered,
    FulfillmentException,
    Cancelled,
    Refunded,
}

/// <summary>
/// The complete legal-transition graph, verbatim from ddd-model.md's Invariants section — enforced
/// here, once, never left to caller discipline. "Any transition attempted outside this graph... is
/// rejected as a no-op, never silently applied."
/// </summary>
public static class OrderStatusTransitions
{
    private static readonly IReadOnlyDictionary<OrderStatus, OrderStatus[]> Legal = new Dictionary<OrderStatus, OrderStatus[]>
    {
        // Created -> Reserved (InventoryReserved), or Cancelled (client cancel / no compensation needed yet).
        [OrderStatus.Created] = [OrderStatus.Reserved, OrderStatus.Cancelled],

        // Reserved -> Paid (PaymentCompleted), or Cancelled (client cancel / PaymentFailed compensation).
        [OrderStatus.Reserved] = [OrderStatus.Paid, OrderStatus.Cancelled],

        // Paid -> Shipped (ShipmentDispatched); FulfillmentException (ShipmentCreationFailed / sweep);
        // Cancelled (client cancel, pre-Shipped only per the API layer's own guard); Refunded (ChargebackReceived carve-out).
        [OrderStatus.Paid] = [OrderStatus.Shipped, OrderStatus.FulfillmentException, OrderStatus.Cancelled, OrderStatus.Refunded],

        // Shipped -> Delivered (DeliveryStatusUpdated terminal value); Refunded (ChargebackReceived carve-out).
        // Cancelled is deliberately absent — illegal from Shipped onward (requirement-spec Open Questions resolution #2).
        [OrderStatus.Shipped] = [OrderStatus.Delivered, OrderStatus.Refunded],

        // Delivered -> Refunded, via the external refund saga reporting back, or ChargebackReceived.
        [OrderStatus.Delivered] = [OrderStatus.Refunded],

        // FulfillmentException -> Paid (manual retry) or Cancelled (manual cancel-with-refund), or
        // Refunded (ChargebackReceived — a held order has already had PaymentCompleted, ADR-0012).
        [OrderStatus.FulfillmentException] = [OrderStatus.Paid, OrderStatus.Cancelled, OrderStatus.Refunded],

        // Cancelled and Refunded are mutually exclusive terminal states — no legal exit from either.
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Refunded] = [],
    };

    public static bool IsLegalTransition(OrderStatus from, OrderStatus to) =>
        Legal.TryGetValue(from, out var allowed) && allowed.Contains(to);
}
