using FluentAssertions;
using KartOrderService.Domain.Orders;
using Xunit;

namespace KartOrderService.UnitTests.Domain;

public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Principal = "system:test";

    private static Order CreateOrder(int itemCount = 1)
    {
        var items = Enumerable.Range(0, itemCount)
            .Select(i => new CreateOrderLineItem($"SKU-{i}", 1, 10m, "USD", Guid.NewGuid()))
            .ToList();

        return Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(), items, Principal, Now);
    }

    private static Order ReserveOrder(Order order)
    {
        foreach (var item in order.LineItems)
        {
            order.MarkLineItemReservationConfirmed(item.Sku, Principal, Now);
        }

        order.TryAdvanceToReserved(Principal, Now).IsSuccess.Should().BeTrue();
        return order;
    }

    private static Order PayOrder(Order order)
    {
        ReserveOrder(order);
        order.TryAdvanceToPaid(Guid.NewGuid(), Principal, Now).IsSuccess.Should().BeTrue();
        return order;
    }

    [Fact]
    public void Create_SetsInitialStatusToCreated_AndRaisesOrderCreatedEvent()
    {
        var order = CreateOrder();

        order.Status.Should().Be(OrderStatus.Created);
        order.Events.Should().ContainSingle(e => e.EventType == "OrderCreated" && e.Sequence == 1);
    }

    [Fact]
    public void MatchesRequest_IdenticalItems_ReturnsTrue()
    {
        var order = CreateOrder();
        var items = order.LineItems.Select(li => (li.Sku, li.Qty, li.UnitPrice)).ToList();

        order.MatchesRequest(order.UserId, order.Currency, items).Should().BeTrue();
    }

    [Fact]
    public void MatchesRequest_DifferentUserId_ReturnsFalse()
    {
        var order = CreateOrder();
        var items = order.LineItems.Select(li => (li.Sku, li.Qty, li.UnitPrice)).ToList();

        order.MatchesRequest(Guid.NewGuid(), order.Currency, items).Should().BeFalse();
    }

    [Fact]
    public void TryAdvanceToReserved_WaitsUntilAllLineItemsConfirmed()
    {
        var order = CreateOrder(itemCount: 2);

        order.MarkLineItemReservationConfirmed(order.LineItems[0].Sku, Principal, Now);
        var partialResult = order.TryAdvanceToReserved(Principal, Now);

        partialResult.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Created, "not every line item's reservation is confirmed yet");

        order.MarkLineItemReservationConfirmed(order.LineItems[1].Sku, Principal, Now);
        var completeResult = order.TryAdvanceToReserved(Principal, Now);

        completeResult.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Reserved);
    }

    [Fact]
    public void TryAdvanceToReserved_WhenAlreadyPastCreated_IsIdempotentNoOp()
    {
        var order = CreateOrder();
        ReserveOrder(order);
        var eventCountBefore = order.Events.Count;

        var result = order.TryAdvanceToReserved(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Events.Should().HaveCount(eventCountBefore, "a duplicate InventoryReserved redelivery must not double-advance the saga");
    }

    [Fact]
    public void TryAdvanceToPaid_FromReserved_TransitionsAndRaisesOrderConfirmed_AndCapturesPaymentIntentId()
    {
        var order = CreateOrder();
        ReserveOrder(order);
        var paymentIntentId = Guid.NewGuid();

        var result = order.TryAdvanceToPaid(paymentIntentId, Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
        order.PaymentIntentId.Should().Be(paymentIntentId);
        order.Events.Should().Contain(e => e.EventType == "OrderConfirmed");
    }

    [Fact]
    public void TryAdvanceToPaid_DuplicateDelivery_DoesNotOverwritePaymentIntentId()
    {
        var order = CreateOrder();
        ReserveOrder(order);
        var firstPaymentIntentId = Guid.NewGuid();
        order.TryAdvanceToPaid(firstPaymentIntentId, Principal, Now);

        order.TryAdvanceToPaid(Guid.NewGuid(), Principal, Now); // a second, stale PaymentCompleted

        order.PaymentIntentId.Should().Be(firstPaymentIntentId);
    }

    [Fact]
    public void TryAdvanceToPaid_FromCreated_IsRejected()
    {
        var order = CreateOrder(); // still Created — InventoryReserved never arrived

        var result = order.TryAdvanceToPaid(Guid.NewGuid(), Principal, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
    }

    [Fact]
    public void TryCancel_FromCreated_TransitionsAndRaisesOrderCancelled()
    {
        var order = CreateOrder();

        var result = order.TryCancel("client_cancel", Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        order.Events.Should().Contain(e => e.EventType == "OrderCancelled");
    }

    [Fact]
    public void TryCancel_FromPaid_IsLegal()
    {
        var order = CreateOrder();
        PayOrder(order);

        var result = order.TryCancel("client_cancel", Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void TryCancel_FromShipped_IsRejected()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryAdvanceToShipped("TRACK-1", Principal, Now);

        var result = order.TryCancel("client_cancel", Principal, Now);

        result.IsFailure.Should().BeTrue("requirement-spec Open Questions resolution #2: cancellation is illegal from Shipped onward");
    }

    [Fact]
    public void TryCancel_WhenAlreadyCancelled_IsIdempotentNoOp()
    {
        var order = CreateOrder();
        order.TryCancel("client_cancel", Principal, Now);
        var eventCountBefore = order.Events.Count;

        var result = order.TryCancel("client_cancel", Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Events.Should().HaveCount(eventCountBefore);
    }

    [Fact]
    public void TryAdvanceToDelivered_FromShipped_TransitionsAndRaisesOrderDelivered()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryAdvanceToShipped("TRACK-1", Principal, Now);

        var result = order.TryAdvanceToDelivered(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Delivered);
        order.Events.Should().Contain(e => e.EventType == "OrderDelivered");
    }

    [Fact]
    public void TryAdvanceToDelivered_WhenAlreadyDelivered_IsIdempotentNoOp()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryAdvanceToShipped("TRACK-1", Principal, Now);
        order.TryAdvanceToDelivered(Principal, Now);
        var eventCountBefore = order.Events.Count;

        var result = order.TryAdvanceToDelivered(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Events.Should().HaveCount(eventCountBefore, "a duplicate terminal DeliveryStatusUpdated must not re-publish OrderDelivered");
    }

    [Fact]
    public void TryAdvanceToDelivered_WhenNotYetShipped_IsRejected()
    {
        var order = CreateOrder();
        PayOrder(order); // Paid, but ShipmentDispatched never consumed

        var result = order.TryAdvanceToDelivered(Principal, Now);

        result.IsFailure.Should().BeTrue("design-decisions.md's ordering guard: no transition may skip states");
    }

    [Fact]
    public void TryEnterFulfillmentException_FromPaid_Transitions()
    {
        var order = CreateOrder();
        PayOrder(order);

        var result = order.TryEnterFulfillmentException(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.FulfillmentException);
    }

    [Fact]
    public void TryRetryFromFulfillmentException_TransitionsBackToPaid_AndRepublishesOrderConfirmed()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryEnterFulfillmentException(Principal, Now);

        var result = order.TryRetryFromFulfillmentException(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
        order.Events.Count(e => e.EventType == "OrderConfirmed").Should().Be(2, "once on the original PaymentCompleted, once more on this retry");
    }

    [Fact]
    public void TryCancel_FromFulfillmentException_IsLegal()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryEnterFulfillmentException(Principal, Now);

        var result = order.TryCancel("fulfillment_exception_cancel", Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Theory]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Delivered)]
    public void TryReactToChargeback_FromAnyPostPaidState_TransitionsToRefunded(OrderStatus targetStatus)
    {
        var order = CreateOrder();
        PayOrder(order);
        if (targetStatus is OrderStatus.Shipped or OrderStatus.Delivered)
        {
            order.TryAdvanceToShipped("TRACK-1", Principal, Now);
        }

        if (targetStatus is OrderStatus.Delivered)
        {
            order.TryAdvanceToDelivered(Principal, Now);
        }

        var result = order.TryReactToChargeback(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Refunded);
    }

    [Fact]
    public void TryReactToChargeback_FromFulfillmentException_IsLegal_ADR0012Carveout()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryEnterFulfillmentException(Principal, Now);

        var result = order.TryReactToChargeback(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Refunded);
    }

    [Fact]
    public void TryReactToChargeback_FromCreated_IsRejected()
    {
        var order = CreateOrder(); // never paid — a chargeback here would be a genuine anomaly

        var result = order.TryReactToChargeback(Principal, Now);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void TryReactToChargeback_WhenAlreadyRefunded_IsIdempotentNoOp()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryReactToChargeback(Principal, Now);
        var eventCountBefore = order.Events.Count;

        var result = order.TryReactToChargeback(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Events.Should().HaveCount(eventCountBefore, "never a second Inventory release attempt or a second Refunded transition");
    }

    [Fact]
    public void CancelledAndRefunded_AreMutuallyExclusiveTerminalStates()
    {
        var order = CreateOrder();
        order.TryCancel("client_cancel", Principal, Now);

        OrderStatusTransitions.IsLegalTransition(OrderStatus.Cancelled, OrderStatus.Refunded).Should().BeFalse();
    }

    [Fact]
    public void RecordCompensationTriggered_DoesNotChangeStatus_ButAppendsEvent()
    {
        var order = CreateOrder();
        var statusBefore = order.Status;

        order.RecordCompensationTriggered("payment_failed", Principal, Now);

        order.Status.Should().Be(statusBefore);
        order.Events.Should().Contain(e => e.EventType == "OrderCompensationTriggered");
    }

    [Fact]
    public void EventSequence_IsMonotonicPerOrder()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryAdvanceToShipped("TRACK-1", Principal, Now);
        order.TryAdvanceToDelivered(Principal, Now);

        var sequences = order.Events.Select(e => e.Sequence).ToList();
        sequences.Should().BeInAscendingOrder();
        sequences.Should().OnlyHaveUniqueItems();
    }

    // ── Flow #7: UpdateShippingAddress ──────────────────────────────────────────────────────────

    private static ShippingAddress SampleAddress() =>
        new("Ada Lovelace", "1 Analytical Ave", null, "London", "LDN", "EC1", "GB", "+44 20 0000 0000");

    [Fact]
    public void UpdateShippingAddress_FromCreated_SucceedsAndRecordsEvent_WithoutChangingStatus()
    {
        var order = CreateOrder();

        var result = order.UpdateShippingAddress(SampleAddress(), Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.ShippingAddress.Should().NotBeNull();
        order.ShippingAddress!.RecipientName.Should().Be("Ada Lovelace");
        order.Status.Should().Be(OrderStatus.Created, "an address edit never changes the saga state");
        order.Events.Should().Contain(e => e.EventType == "OrderShippingAddressUpdated");
    }

    [Fact]
    public void UpdateShippingAddress_FromPaid_IsLegal()
    {
        var order = CreateOrder();
        PayOrder(order);

        var result = order.UpdateShippingAddress(SampleAddress(), Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.ShippingAddress.Should().NotBeNull();
    }

    [Fact]
    public void UpdateShippingAddress_WhenShipped_ReturnsConflict()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryAdvanceToShipped("TRACK-1", Principal, Now);

        var result = order.UpdateShippingAddress(SampleAddress(), Principal, Now);

        result.IsFailure.Should().BeTrue("a courier/label already exists past Shipped");
        result.Error.Code.Should().Be("conflict");
        order.ShippingAddress.Should().BeNull();
    }

    [Fact]
    public void UpdateShippingAddress_WhenCancelled_ReturnsConflict()
    {
        var order = CreateOrder();
        order.TryCancel("client_cancel", Principal, Now);

        var result = order.UpdateShippingAddress(SampleAddress(), Principal, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
    }

    // ── Flow #7: AdminAdvanceStatus ─────────────────────────────────────────────────────────────

    [Fact]
    public void AdminAdvanceStatus_PaidToShipped_Succeeds()
    {
        var order = CreateOrder();
        PayOrder(order);

        var result = order.AdminAdvanceStatus(OrderStatus.Shipped, "carrier picked up manually", Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Shipped);
        order.Events.Should().Contain(e => e.EventType == "OrderStatusChangedByAdmin");
    }

    [Fact]
    public void AdminAdvanceStatus_WhenAlreadyAtTarget_IsIdempotentNoOp()
    {
        var order = CreateOrder();
        PayOrder(order);
        order.TryAdvanceToShipped("TRACK-1", Principal, Now);
        var eventCountBefore = order.Events.Count;

        var result = order.AdminAdvanceStatus(OrderStatus.Shipped, "already shipped", Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Events.Should().HaveCount(eventCountBefore, "advancing to the current status must not append an event");
    }

    [Fact]
    public void AdminAdvanceStatus_IllegalTransition_ReturnsConflict()
    {
        var order = CreateOrder();
        PayOrder(order); // Paid → Delivered is not a legal single-step transition

        var result = order.AdminAdvanceStatus(OrderStatus.Delivered, "skip ahead", Principal, Now);

        result.IsFailure.Should().BeTrue("Transition re-validates the legal-transition graph");
        result.Error.Code.Should().Be("conflict");
        order.Status.Should().Be(OrderStatus.Paid);
    }

    // ── Flow #7: RequestShipment ────────────────────────────────────────────────────────────────

    [Fact]
    public void RequestShipment_WhenPaid_SucceedsAndRecordsEvent_WithoutChangingStatus()
    {
        var order = CreateOrder();
        PayOrder(order);

        var result = order.RequestShipment(Principal, Now);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid, "requesting shipment is intent only, not a state change");
        order.Events.Should().Contain(e => e.EventType == "OrderShipmentRequested");
    }

    [Fact]
    public void RequestShipment_WhenNotPaid_ReturnsConflict()
    {
        var order = CreateOrder(); // still Created

        var result = order.RequestShipment(Principal, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
        order.Events.Should().NotContain(e => e.EventType == "OrderShipmentRequested");
    }
}
