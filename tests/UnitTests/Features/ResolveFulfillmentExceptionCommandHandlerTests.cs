using FluentAssertions;
using Kart.Shared.Auditing;
using KartOrderService.Application.Common.Compensation;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Features.ResolveFulfillmentException;
using KartOrderService.Domain.Orders;
using KartOrderService.UnitTests.TestSupport;
using NSubstitute;
using Xunit;

namespace KartOrderService.UnitTests.Features;

/// <summary>Coverage for the previously-untested ORD-12 handler — both `retry` and `cancel` actions, including the no-PaymentIntentId conflict case.</summary>
public sealed class ResolveFulfillmentExceptionCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _orderRepository = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IInventoryClient _inventoryClient = Substitute.For<IInventoryClient>();
    private readonly IPaymentClient _paymentClient = Substitute.For<IPaymentClient>();
    private readonly ICurrentPrincipal _currentPrincipal = Substitute.For<ICurrentPrincipal>();
    private readonly IAuditLogWriter _auditLogWriter = Substitute.For<IAuditLogWriter>();

    public ResolveFulfillmentExceptionCommandHandlerTests()
    {
        _currentPrincipal.ActingPrincipal.Returns("admin-1");
        _currentPrincipal.Kind.Returns("service");
    }

    private ResolveFulfillmentExceptionCommandHandler CreateHandler() => new(
        _orderRepository, _unitOfWork, new InventoryReleaseCompensator(_inventoryClient), _paymentClient, _currentPrincipal, new FakeTimeProvider(Now), _auditLogWriter);

    private static Order NewFulfillmentExceptionOrder(Guid? reservationId = null)
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(),
            [new CreateOrderLineItem("SKU-1", 1, 10m, "USD", reservationId ?? Guid.NewGuid())], "user-1", Now);
        foreach (var item in order.LineItems)
        {
            order.MarkLineItemReservationConfirmed(item.Sku, "system:test", Now);
        }

        order.TryAdvanceToReserved("system:test", Now);
        order.TryAdvanceToPaid(Guid.NewGuid(), "system:test", Now); // captures a PaymentIntentId
        order.TryEnterFulfillmentException("system:test", Now);
        return order;
    }

    [Fact]
    public async Task Handle_OrderNotFound_ReturnsNotFound()
    {
        _orderRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        var result = await CreateHandler().Handle(new ResolveFulfillmentExceptionCommand(Guid.NewGuid(), "retry", "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Handle_NotInFulfillmentException_ReturnsConflict()
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(),
            [new CreateOrderLineItem("SKU-1", 1, 10m, "USD", Guid.NewGuid())], "user-1", Now); // Created
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new ResolveFulfillmentExceptionCommand(order.OrderId, "retry", "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
    }

    [Fact]
    public async Task Handle_RetryAction_TransitionsBackToPaid_CommitsAndAudits()
    {
        var order = NewFulfillmentExceptionOrder();
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new ResolveFulfillmentExceptionCommand(order.OrderId, "retry", "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Paid");
        await _paymentClient.DidNotReceive().RefundAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
        await _auditLogWriter.Received(1).WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CancelAction_RefundsReleasesInventoryAndCancels()
    {
        var reservationId = Guid.NewGuid();
        var order = NewFulfillmentExceptionOrder(reservationId);
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);
        _paymentClient.RefundAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentRefundResult(PaymentRefundOutcome.Accepted));

        var result = await CreateHandler().Handle(new ResolveFulfillmentExceptionCommand(order.OrderId, "cancel", "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Cancelled");
        await _paymentClient.Received(1).RefundAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _inventoryClient.Received(1).ReleaseAsync(reservationId, Arg.Any<CancellationToken>());
        await _auditLogWriter.Received(1).WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CancelAction_RefundFails_LeavesOrderInFulfillmentException()
    {
        var order = NewFulfillmentExceptionOrder();
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);
        _paymentClient.RefundAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentRefundResult(PaymentRefundOutcome.Unavailable));

        var result = await CreateHandler().Handle(new ResolveFulfillmentExceptionCommand(order.OrderId, "cancel", "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("refund_failed");
        order.Status.Should().Be(OrderStatus.FulfillmentException);
        await _unitOfWork.DidNotReceive().CommitTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CancelAction_NoPaymentIntentId_ReturnsConflict_NeverRefunds()
    {
        // A FulfillmentException order that never captured a PaymentIntentId is not reachable via the
        // public transition path (Paid always captures one), so force the state via reflection to
        // exercise the handler's explicit guard.
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(),
            [new CreateOrderLineItem("SKU-1", 1, 10m, "USD", Guid.NewGuid())], "user-1", Now);
        typeof(Order).GetProperty(nameof(Order.Status))!.SetValue(order, OrderStatus.FulfillmentException);
        order.PaymentIntentId.Should().BeNull();

        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new ResolveFulfillmentExceptionCommand(order.OrderId, "cancel", "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
        await _paymentClient.DidNotReceive().RefundAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
