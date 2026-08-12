using System.Text.Json;
using Kart.Shared.Domain;

namespace KartOrderService.Domain.Orders;

/// <summary>
/// ddd-model.md's `Order` aggregate root — the single authoritative record of an order's
/// lifecycle/Saga state (requirement-spec §4 "Sole orchestrator invariant"). No separate `Saga`
/// aggregate exists (Modeling Decision #5): `Order` *is* the Saga instance, and `Status` *is* its
/// current state. Every transition method here is the in-memory half of the compare-and-swap
/// concurrency mechanism (design-decisions.md) — `Status` is configured as an EF Core concurrency
/// token (`Infrastructure/Persistence/Configurations/OrderConfiguration.cs`), so `SaveChangesAsync`
/// itself performs the literal `UPDATE orders SET status=$new WHERE order_id=$id AND status=
/// $expected` database-design.md specifies, throwing `DbUpdateConcurrencyException` on zero rows
/// affected — callers translate that into their own retry/reject/no-op policy.
/// </summary>
public sealed class Order
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public Guid OrderId { get; private set; }

    public Guid UserId { get; private set; }

    public OrderStatus Status { get; private set; }

    public decimal TotalAmount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>ddd-model.md's `IdempotencyKey` value object — unique per Order (unlike Payment, no separate ledger aggregate; see ddd-model.md's Value Objects contrast).</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>
    /// Addendum (`contracts/README.md` #3): `database-design.md`'s `orders` table has no such
    /// column, but ORD-12's `resolve-fulfillment-exception` `cancel` action and a future refund
    /// path both need to call Payment's `POST /payments/{id}/refund`, which requires knowing which
    /// `PaymentIntent` this order's charge belongs to. Captured from `PaymentCompleted`'s own
    /// payload (`paymentIntentId`) the one time Order consumes it (ORD-7) — never sourced from a
    /// client-supplied value.
    /// </summary>
    public Guid? PaymentIntentId { get; private set; }

    /// <summary>
    /// Addendum (`contracts/README.md` #6): `database-design.md`'s `orders` table has no such
    /// column, but Delivery Tracking's `DeliveryStatusUpdated` payload is deliberately just
    /// `{trackingId, status}` — it never carries `orderId` (`kart-delivery-tracking-service/
    /// event-contract.md`'s own "Payload Resolution" note). Order can only tie that event back to
    /// itself by remembering the `trackingId` Shipping's own `ShipmentDispatched` (which DOES carry
    /// `orderId`) already gave it, one step earlier in the saga (ORD-9).
    /// </summary>
    public string? TrackingId { get; private set; }

    /// <summary>
    /// Flow #7 (Order Management, Admin): the delivery address, attached/corrected by an admin while
    /// the order has not yet shipped. Null on every order created before this field existed and on
    /// any order for which no admin ever set one. An EF Core owned value object on the `orders` table.
    /// </summary>
    public ShippingAddress? ShippingAddress { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public string UpdatedBy { get; private set; } = string.Empty;

    private readonly List<OrderLineItem> _lineItems = [];
    public IReadOnlyList<OrderLineItem> LineItems => _lineItems.AsReadOnly();

    private readonly List<OrderEvent> _events = [];
    public IReadOnlyList<OrderEvent> Events => _events.AsReadOnly();

    /// <summary>EF Core materialization only.</summary>
    private Order()
    {
    }

    /// <summary>
    /// ORD-1: the one insert guarded by the unique constraint on <see cref="IdempotencyKey"/>
    /// instead of a compare-and-swap (database-design.md — "the one exception"). Each
    /// <paramref name="items"/> entry's `ReservationId` must already be populated by the caller's
    /// synchronous per-line `POST /inventory/reserve` fan-out (ORD-1's handler) before this is
    /// called — Order never calls Inventory itself.
    /// </summary>
    public static Order Create(
        Guid orderId,
        Guid userId,
        string idempotencyKey,
        IReadOnlyList<CreateOrderLineItem> items,
        string actingPrincipal,
        DateTimeOffset now)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("An order must have at least one line item.", nameof(items));
        }

        var currency = items[0].Currency;
        var total = items.Sum(i => i.UnitPrice * i.Qty);

        var order = new Order
        {
            OrderId = orderId,
            UserId = userId,
            Status = OrderStatus.Created,
            TotalAmount = total,
            Currency = currency,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = actingPrincipal,
            UpdatedBy = actingPrincipal,
        };

        foreach (var item in items)
        {
            order._lineItems.Add(new OrderLineItem(Guid.NewGuid(), orderId, item.Sku, item.Qty, item.UnitPrice, item.Currency, item.ReservationId, actingPrincipal, now));
        }

        order._events.Add(new OrderEvent(
            Guid.NewGuid(), orderId, sequence: 1, fromStatus: null, toStatus: OrderStatus.Created,
            eventType: "OrderCreated",
            payload: JsonSerializer.Serialize(new
            {
                orderId,
                userId,
                items = items.Select(i => new { sku = i.Sku, qty = i.Qty, unitPrice = new { amount = i.UnitPrice, currency = i.Currency } }),
                total,
                currency,
            }, PayloadOptions),
            actingPrincipal, now));

        return order;
    }

    /// <summary>
    /// requirement-spec Open Questions resolution #3's replay check — compares a candidate replay
    /// request against this already-persisted order. Identical ⇒ safe replay (202/current
    /// representation); different ⇒ the caller returns 422.
    /// </summary>
    public bool MatchesRequest(Guid userId, string currency, IReadOnlyList<(string Sku, int Qty, decimal UnitPrice)> items)
    {
        if (UserId != userId || !string.Equals(Currency, currency, StringComparison.OrdinalIgnoreCase) || _lineItems.Count != items.Count)
        {
            return false;
        }

        var ordered = _lineItems.OrderBy(li => li.Sku).ToList();
        var incoming = items.OrderBy(i => i.Sku).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Sku != incoming[i].Sku || ordered[i].Qty != incoming[i].Qty || ordered[i].UnitPrice != incoming[i].UnitPrice)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>ORD-6: marks one line item's reservation confirmed on consuming `InventoryReserved`. Idempotent (redelivery of the same event is a no-op via <see cref="OrderLineItem.ConfirmReservation"/>).</summary>
    public void MarkLineItemReservationConfirmed(string sku, string actingPrincipal, DateTimeOffset now)
    {
        var item = _lineItems.FirstOrDefault(li => li.Sku == sku);
        item?.ConfirmReservation(actingPrincipal, now);
    }

    public bool AllLineItemsReservationConfirmed => _lineItems.All(li => li.ReservationConfirmedAt is not null);

    /// <summary>ORD-6: `Created→Reserved`, only once every line item's reservation is confirmed. No published event (internal-only transition).</summary>
    public Result TryAdvanceToReserved(string actingPrincipal, DateTimeOffset now)
    {
        if (Status != OrderStatus.Created)
        {
            // Created is the aggregate's very first state - anything else means this order has
            // already moved on (to Reserved or beyond, or was cancelled) - a duplicate
            // InventoryReserved redelivery is an idempotent no-op regardless of which later state.
            return Result.Success();
        }

        if (!AllLineItemsReservationConfirmed)
        {
            return Result.Success(); // still waiting on other line items' InventoryReserved events.
        }

        return Transition(OrderStatus.Reserved, eventType: null, payload: null, actingPrincipal, now);
    }

    /// <summary>ORD-7: `Reserved→Paid`, publishes `OrderConfirmed` (ADR-0002 — as soon as `PaymentCompleted` is received, not gated on shipment). Captures <paramref name="paymentIntentId"/> for the later refund path (`PaymentIntentId` addendum).</summary>
    public Result TryAdvanceToPaid(Guid paymentIntentId, string actingPrincipal, DateTimeOffset now)
    {
        PaymentIntentId ??= paymentIntentId; // idempotent — never overwrites an already-captured value on redelivery.
        return TryAdvance(OrderStatus.Reserved, OrderStatus.Paid, "OrderConfirmed", new { orderId = OrderId }, actingPrincipal, now);
    }

    /// <summary>ORD-9: `Paid→Shipped`, informational only — no published event (ADR-0002). Captures <paramref name="trackingId"/> so ORD-10 can later resolve `DeliveryStatusUpdated`'s `{trackingId, status}`-only payload back to this order.</summary>
    public Result TryAdvanceToShipped(string trackingId, string actingPrincipal, DateTimeOffset now)
    {
        TrackingId ??= trackingId; // idempotent — never overwrites an already-captured value on redelivery.
        return TryAdvance(OrderStatus.Paid, OrderStatus.Shipped, eventType: null, payload: null, actingPrincipal, now);
    }

    /// <summary>ORD-10: `Shipped→Delivered`, publishes `OrderDelivered` (ADR-0005). Idempotent no-op if already `Delivered`; `Failure` if not yet `Shipped` — the caller (consumer) NACKs/requeues on `Failure`, per design-decisions.md's ordering guard, rather than skip the invariant.</summary>
    public Result TryAdvanceToDelivered(string actingPrincipal, DateTimeOffset now) =>
        TryAdvance(OrderStatus.Shipped, OrderStatus.Delivered, "OrderDelivered", new { orderId = OrderId, deliveredAt = now }, actingPrincipal, now);

    /// <summary>ORD-11: `Paid→FulfillmentException` (ADR-0015 — explicit `ShipmentCreationFailed`, or the reconciliation sweep's Shipping-await threshold). No published event.</summary>
    public Result TryEnterFulfillmentException(string actingPrincipal, DateTimeOffset now) =>
        TryAdvance(OrderStatus.Paid, OrderStatus.FulfillmentException, eventType: null, payload: null, actingPrincipal, now);

    /// <summary>ORD-12 `retry` action: `FulfillmentException→Paid`, republishes `OrderConfirmed` (same signal Shipping already knows how to handle idempotently).</summary>
    public Result TryRetryFromFulfillmentException(string actingPrincipal, DateTimeOffset now) =>
        TryAdvance(OrderStatus.FulfillmentException, OrderStatus.Paid, "OrderConfirmed", new { orderId = OrderId }, actingPrincipal, now);

    /// <summary>
    /// ddd-model.md Modeling Decision #3: fired at the *start* of a compensation sequence (before
    /// the terminal write), reused across all three compensation-initiating triggers (pre-
    /// confirmation `PaymentFailed`, client-initiated cancel, `FulfillmentException`→`Cancelled`).
    /// Does not itself change <see cref="Status"/>.
    /// </summary>
    public void RecordCompensationTriggered(string reason, string actingPrincipal, DateTimeOffset now) =>
        RecordEvent("OrderCompensationTriggered", new { orderId = OrderId, reason }, actingPrincipal, now);

    /// <summary>
    /// ORD-5/ORD-8/ORD-12(cancel): `{Created,Reserved,Paid,FulfillmentException}→Cancelled`.
    /// Idempotent no-op if already `Cancelled`; `Failure` (409) if illegal from the current status
    /// (e.g. already `Shipped` or later — requirement-spec Open Questions resolution #2).
    /// </summary>
    public Result TryCancel(string reason, string actingPrincipal, DateTimeOffset now)
    {
        if (Status == OrderStatus.Cancelled)
        {
            return Result.Success();
        }

        if (!OrderStatusTransitions.IsLegalTransition(Status, OrderStatus.Cancelled))
        {
            return Result.Failure(Error.Conflict($"Order {OrderId} cannot be cancelled from status '{Status}'."));
        }

        return Transition(OrderStatus.Cancelled, "OrderCancelled", new { orderId = OrderId, reason }, actingPrincipal, now);
    }

    /// <summary>
    /// ORD-13 (and the chargeback carve-out generally): direct `→Refunded` from any `Paid`+ state,
    /// including `FulfillmentException` (ADR-0012). Idempotent no-op if already `Refunded`. No
    /// published event (Modeling Decision #4 — no new Order-published event for this reaction).
    /// </summary>
    public Result TryReactToChargeback(string actingPrincipal, DateTimeOffset now)
    {
        if (Status == OrderStatus.Refunded)
        {
            return Result.Success();
        }

        if (!OrderStatusTransitions.IsLegalTransition(Status, OrderStatus.Refunded))
        {
            return Result.Failure(Error.Conflict($"Order {OrderId} cannot be refunded via chargeback from status '{Status}'."));
        }

        return Transition(OrderStatus.Refunded, eventType: null, payload: null, actingPrincipal, now);
    }

    /// <summary>
    /// Flow #7 (Order Management, Admin): attach/correct the delivery <paramref name="address"/> on an
    /// order that has not yet shipped. Legal only while <see cref="Status"/> is NOT
    /// <see cref="OrderStatus.Shipped"/>/<see cref="OrderStatus.Delivered"/>/<see cref="OrderStatus.Cancelled"/>/
    /// <see cref="OrderStatus.Refunded"/> — past Shipped a courier label already exists against the old
    /// address, so a correction there is a returns-flow concern, not an address edit. Does NOT change
    /// <see cref="Status"/>; records an `OrderShippingAddressUpdated` event (published for downstream
    /// consumers) and bumps <see cref="UpdatedAt"/>/<see cref="UpdatedBy"/>.
    /// </summary>
    public Result UpdateShippingAddress(ShippingAddress address, string actingPrincipal, DateTimeOffset now)
    {
        if (Status is OrderStatus.Shipped or OrderStatus.Delivered or OrderStatus.Cancelled or OrderStatus.Refunded)
        {
            return Result.Failure(Error.Conflict($"Order {OrderId} shipping address cannot be changed from status '{Status}' — a courier/label already exists past Shipped; use the returns/refund flow instead."));
        }

        ShippingAddress = address;
        UpdatedAt = now;
        UpdatedBy = actingPrincipal;

        RecordEvent("OrderShippingAddressUpdated", new { orderId = OrderId, address }, actingPrincipal, now);
        return Result.Success();
    }

    /// <summary>
    /// Flow #7 (Order Management, Admin): an ops-recovery fallback letting an admin manually advance
    /// <see cref="Status"/> when the normal event-driven trigger stalled — the same advances the
    /// automated <see cref="Infrastructure.ReconciliationSweep.ReconciliationSweepHostedService"/>
    /// performs, just admin-initiated and audited here. No-op success if already at
    /// <paramref name="to"/>; otherwise defers to <c>Transition</c>, which itself re-validates
    /// <see cref="OrderStatusTransitions.IsLegalTransition"/> and returns a <c>Conflict</c> for an
    /// illegal move. Deliberately generic/reusable (no target restriction here, matching
    /// <c>Transition</c>) — restricting the reachable targets to {Shipped, Delivered,
    /// FulfillmentException} is a command-level policy decision, not an aggregate invariant.
    /// </summary>
    public Result AdminAdvanceStatus(OrderStatus to, string reason, string actingPrincipal, DateTimeOffset now)
    {
        if (Status == to)
        {
            return Result.Success();
        }

        return Transition(to, "OrderStatusChangedByAdmin", new { orderId = OrderId, fromStatus = Status.ToString(), toStatus = to.ToString(), reason }, actingPrincipal, now);
    }

    /// <summary>
    /// Flow #7 (Order Management, Admin): records an admin's intent to request shipment for a paid
    /// order, via an `OrderShipmentRequested` event — intent only, does NOT change <see cref="Status"/>.
    /// Legal only while <see cref="Status"/> is <see cref="OrderStatus.Paid"/>. The eventual consumer
    /// (`kart-shipping-service`, flow #8) does not exist yet, so today this call's sole job is to
    /// durably record+publish the intent for whenever that consumer arrives — no downstream side
    /// effect is expected yet, and that is intentional.
    /// </summary>
    public Result RequestShipment(string actingPrincipal, DateTimeOffset now)
    {
        if (Status != OrderStatus.Paid)
        {
            return Result.Failure(Error.Conflict($"Order {OrderId} must be 'Paid' to request shipment (current status '{Status}')."));
        }

        RecordEvent("OrderShipmentRequested", new { orderId = OrderId, requestedBy = actingPrincipal, requestedAt = now }, actingPrincipal, now);
        return Result.Success();
    }

    /// <summary>Generic idempotent-advance: already-at-target ⇒ no-op success; wrong pre-state ⇒ Failure (caller's CAS-lost policy); otherwise transitions.</summary>
    private Result TryAdvance(OrderStatus expectedFrom, OrderStatus to, string? eventType, object? payload, string actingPrincipal, DateTimeOffset now)
    {
        if (Status == to)
        {
            return Result.Success();
        }

        if (Status != expectedFrom)
        {
            return Result.Failure(Error.Conflict($"Order {OrderId} cannot transition to '{to}' from status '{Status}' (expected '{expectedFrom}')."));
        }

        return Transition(to, eventType, payload, actingPrincipal, now);
    }

    private Result Transition(OrderStatus to, string? eventType, object? payload, string actingPrincipal, DateTimeOffset now)
    {
        if (!OrderStatusTransitions.IsLegalTransition(Status, to))
        {
            return Result.Failure(Error.Conflict($"Cannot transition Order {OrderId} from '{Status}' to '{to}'."));
        }

        var from = Status;
        Status = to;
        UpdatedAt = now;
        UpdatedBy = actingPrincipal;

        _events.Add(new OrderEvent(
            Guid.NewGuid(), OrderId, NextSequence(), from, to, eventType,
            payload is null ? null : JsonSerializer.Serialize(payload, PayloadOptions),
            actingPrincipal, now));

        return Result.Success();
    }

    private void RecordEvent(string eventType, object? payload, string actingPrincipal, DateTimeOffset now)
    {
        _events.Add(new OrderEvent(
            Guid.NewGuid(), OrderId, NextSequence(), Status, Status, eventType,
            JsonSerializer.Serialize(payload, PayloadOptions),
            actingPrincipal, now));
    }

    private int NextSequence() => _events.Count == 0 ? 1 : _events.Max(e => e.Sequence) + 1;
}

/// <summary>Input shape for <see cref="Order.Create"/> — <see cref="ReservationId"/> must already be populated by ORD-1's synchronous per-line Inventory reserve fan-out.</summary>
public sealed record CreateOrderLineItem(string Sku, int Qty, decimal UnitPrice, string Currency, Guid? ReservationId);
