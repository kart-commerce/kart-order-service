using FluentAssertions;
using Kart.Shared.Auditing;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Features.UpdateOrderShippingAddress;
using KartOrderService.Domain.Orders;
using KartOrderService.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartOrderService.UnitTests.Features;

public sealed class UpdateOrderShippingAddressCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _orderRepository = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentPrincipal _currentPrincipal = Substitute.For<ICurrentPrincipal>();
    private readonly IAuditLogWriter _auditLogWriter = Substitute.For<IAuditLogWriter>();

    public UpdateOrderShippingAddressCommandHandlerTests()
    {
        _currentPrincipal.ActingPrincipal.Returns("admin-1");
        _currentPrincipal.Kind.Returns("service");
    }

    private UpdateOrderShippingAddressCommandHandler CreateHandler() => new(
        _orderRepository, _unitOfWork, _currentPrincipal, new FakeTimeProvider(Now), _auditLogWriter, NullLogger<UpdateOrderShippingAddressCommandHandler>.Instance);

    private static Order NewCreatedOrder() =>
        Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(),
            [new CreateOrderLineItem("SKU-1", 1, 10m, "USD", Guid.NewGuid())], "user-1", Now);

    private static UpdateOrderShippingAddressCommand Command(Guid orderId) =>
        new(orderId, "Ada Lovelace", "1 Analytical Ave", null, "London", "LDN", "EC1", "GB", "+44 20 0000 0000", "key-1");

    [Fact]
    public async Task Handle_OrderNotFound_ReturnsNotFound()
    {
        _orderRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        var result = await CreateHandler().Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("not_found");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CreatedOrder_SetsAddressCommitsAndAudits()
    {
        var order = NewCreatedOrder();
        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(Command(order.OrderId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ShippingAddress.Should().NotBeNull();
        result.Value.ShippingAddress!.City.Should().Be("London");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
        await _auditLogWriter.Received(1).WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShippedOrder_ReturnsConflict_NeverSaves()
    {
        var order = NewCreatedOrder();
        foreach (var item in order.LineItems)
        {
            order.MarkLineItemReservationConfirmed(item.Sku, "system:test", Now);
        }

        order.TryAdvanceToReserved("system:test", Now);
        order.TryAdvanceToPaid(Guid.NewGuid(), "system:test", Now);
        order.TryAdvanceToShipped("TRACK-1", "system:test", Now);

        _orderRepository.GetByIdAsync(order.OrderId, Arg.Any<CancellationToken>()).Returns(order);

        var result = await CreateHandler().Handle(Command(order.OrderId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
