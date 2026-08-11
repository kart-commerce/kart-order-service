using FluentAssertions;
using Kart.Shared.Auditing;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Features.AdminUpdateOrderStatus;
using KartOrderService.Domain.Orders;
using KartOrderService.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartOrderService.UnitTests.Features;

public sealed class AdminUpdateOrderStatusCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _orderRepository = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentPrincipal _currentPrincipal = Substitute.For<ICurrentPrincipal>();
    private readonly IAuditLogWriter _auditLogWriter = Substitute.For<IAuditLogWriter>();

    public AdminUpdateOrderStatusCommandHandlerTests()
    {
        _currentPrincipal.ActingPrincipal.Returns("admin-1");
        _currentPrincipal.Kind.Returns("service");
    }

    private AdminUpdateOrderStatusCommandHandler CreateHandler() => new(
        _orderRepository, _unitOfWork, _currentPrincipal, new FakeTimeProvider(Now), _auditLogWriter, NullLogger<AdminUpdateOrderStatusCommandHandler>.Instance);

    private static Order NewPaidOrder()
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(),
            [new CreateOrderLineItem("SKU-1", 1, 10m, "USD", Guid.NewGuid())], "user-1", Now);
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

        var result = await CreateHandler().Handle(new AdminUpdateOrderStatusCommand(Guid.NewGuid(), OrderStatus.Shipped, "reason", "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Handle_PaidToShipped_AdvancesCommitsAndAudits()
    {
        var order = NewPaidOrder();
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new AdminUpdateOrderStatusCommand(order.OrderId, OrderStatus.Shipped, "carrier picked up", "key-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Shipped");
        await _unitOfWork.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
        await _auditLogWriter.Received(1).WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IllegalTransition_ReturnsConflict_NeverSaves()
    {
        var order = NewPaidOrder(); // Paid → Delivered is illegal single-step
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(new AdminUpdateOrderStatusCommand(order.OrderId, OrderStatus.Delivered, "skip", "key-1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
