using FluentAssertions;
using Kart.Shared.Auditing;
using KartOrderService.Application.Common.Compensation;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Features.CancelOrder;
using KartOrderService.Domain.Orders;
using KartOrderService.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartOrderService.UnitTests.Features;

public sealed class CancelOrderCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _orderRepository = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IInventoryClient _inventoryClient = Substitute.For<IInventoryClient>();
    private readonly ICurrentPrincipal _currentPrincipal = Substitute.For<ICurrentPrincipal>();
    private readonly IAuditLogWriter _auditLogWriter = Substitute.For<IAuditLogWriter>();

    public CancelOrderCommandHandlerTests()
    {
        _currentPrincipal.ActingPrincipal.Returns("user-1");
        _currentPrincipal.Kind.Returns("user");
    }

    private CancelOrderCommandHandler CreateHandler() => new(
        _orderRepository, _unitOfWork, new InventoryReleaseCompensator(_inventoryClient), _currentPrincipal, new FakeTimeProvider(Now), _auditLogWriter, NullLogger<CancelOrderCommandHandler>.Instance);

    private static Order NewOrder(Guid? reservationId = null) =>
        Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(),
            [new CreateOrderLineItem("SKU-1", 1, 10m, "USD", reservationId ?? Guid.NewGuid())], "user-1", Now);

    [Fact]
    public async Task Handle_OrderNotFound_ReturnsNotFound()
    {
        _orderRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        var result = await CreateHandler().Handle(new CancelOrderCommand(Guid.NewGuid(), "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Handle_CreatedOrder_ReleasesInventoryAndCancels()
    {
        var reservationId = Guid.NewGuid();
        var order = NewOrder(reservationId);
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new CancelOrderCommand(order.OrderId, "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Cancelled");
        await _inventoryClient.Received(1).ReleaseAsync(reservationId, Arg.Any<CancellationToken>());
        await _auditLogWriter.Received(1).WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyShipped_ReturnsConflict_NeverReleasesInventory()
    {
        var order = NewOrder();
        foreach (var item in order.LineItems)
        {
            order.MarkLineItemReservationConfirmed(item.Sku, "system:test", Now);
        }

        order.TryAdvanceToReserved("system:test", Now);
        order.TryAdvanceToPaid(Guid.NewGuid(), "system:test", Now);
        order.TryAdvanceToShipped("TRACK-1", "system:test", Now);

        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new CancelOrderCommand(order.OrderId, "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
        await _inventoryClient.DidNotReceive().ReleaseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyCancelled_IsIdempotentNoOp_NeverSaves()
    {
        var order = NewOrder();
        order.TryCancel("client_cancel", "system:test", Now);
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new CancelOrderCommand(order.OrderId, "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
