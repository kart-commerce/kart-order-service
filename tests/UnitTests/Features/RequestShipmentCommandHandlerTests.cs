using FluentAssertions;
using Kart.Shared.Auditing;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Features.RequestShipment;
using KartOrderService.Domain.Orders;
using KartOrderService.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartOrderService.UnitTests.Features;

public sealed class RequestShipmentCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _orderRepository = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentPrincipal _currentPrincipal = Substitute.For<ICurrentPrincipal>();
    private readonly IAuditLogWriter _auditLogWriter = Substitute.For<IAuditLogWriter>();

    public RequestShipmentCommandHandlerTests()
    {
        _currentPrincipal.ActingPrincipal.Returns("admin-1");
        _currentPrincipal.Kind.Returns("service");
    }

    private RequestShipmentCommandHandler CreateHandler() => new(
        _orderRepository, _unitOfWork, _currentPrincipal, new FakeTimeProvider(Now), _auditLogWriter, NullLogger<RequestShipmentCommandHandler>.Instance);

    private static Order NewCreatedOrder() =>
        Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(),
            [new CreateOrderLineItem("SKU-1", 1, 10m, "USD", Guid.NewGuid())], "user-1", Now);

    private static Order NewPaidOrder()
    {
        var order = NewCreatedOrder();
        foreach (var item in order.LineItems)
        {
            order.MarkLineItemReservationConfirmed(item.Sku, "system:test", Now);
        }

        order.TryAdvanceToReserved("system:test", Now);
        order.TryAdvanceToPaid(Guid.NewGuid(), "system:test", Now);
        return order;
    }

    [Fact]
    public async Task Handle_OrderNotFound_ReturnsNotFound()
    {
        _orderRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        var result = await CreateHandler().Handle(new RequestShipmentCommand(Guid.NewGuid(), "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Handle_PaidOrder_RecordsIntentCommitsAndAudits_WithoutChangingStatus()
    {
        var order = NewPaidOrder();
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new RequestShipmentCommand(order.OrderId, "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Paid", "requesting shipment is intent only");
        order.Events.Should().Contain(e => e.EventType == "OrderShipmentRequested");
        await _unitOfWork.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
        await _auditLogWriter.Received(1).WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotPaid_ReturnsConflict_NeverSaves()
    {
        var order = NewCreatedOrder(); // still Created
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new RequestShipmentCommand(order.OrderId, "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
