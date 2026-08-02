using FluentAssertions;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Features.CreateOrder;
using KartOrderService.Domain.Orders;
using KartOrderService.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartOrderService.UnitTests.Features;

public sealed class CreateOrderCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _orderRepository = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IInventoryClient _inventoryClient = Substitute.For<IInventoryClient>();
    private readonly ICurrentPrincipal _currentPrincipal = Substitute.For<ICurrentPrincipal>();

    public CreateOrderCommandHandlerTests()
    {
        _currentPrincipal.ActingPrincipal.Returns("user-1");
        _currentPrincipal.Kind.Returns("user");
    }

    private CreateOrderCommandHandler CreateHandler() => new(
        _orderRepository, _unitOfWork, _inventoryClient, _currentPrincipal, new FakeTimeProvider(Now), NullLogger<CreateOrderCommandHandler>.Instance);

    private static CreateOrderCommand SampleCommand(Guid userId, string idempotencyKey = "key-1") =>
        new(idempotencyKey, userId, [new CreateOrderLineItemRequest("SKU-1", 2, 10m)], "USD");

    [Fact]
    public async Task Handle_NewOrder_AllReservationsSucceed_CreatesOrderAndReturnsIt()
    {
        _orderRepository.GetByIdempotencyKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Order?)null);
        _inventoryClient.ReserveAsync(Arg.Any<Guid>(), "SKU-1", 2, Arg.Any<CancellationToken>())
            .Returns(new InventoryReserveResult(InventoryReserveOutcome.Reserved, Guid.NewGuid()));

        var handler = CreateHandler();
        var result = await handler.Handle(SampleCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Created");
        _orderRepository.Received(1).Add(Arg.Any<Order>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReservationInsufficientStock_ReleasesNothingAndReturnsInsufficientStock_NoOrderPersisted()
    {
        _orderRepository.GetByIdempotencyKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Order?)null);
        _inventoryClient.ReserveAsync(Arg.Any<Guid>(), "SKU-1", 2, Arg.Any<CancellationToken>())
            .Returns(new InventoryReserveResult(InventoryReserveOutcome.InsufficientStock, null));

        var handler = CreateHandler();
        var result = await handler.Handle(SampleCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("insufficient_stock");
        _orderRepository.DidNotReceive().Add(Arg.Any<Order>());
    }

    [Fact]
    public async Task Handle_PartialReservationFailure_ReleasesTheSuccessfulLines()
    {
        var command = new CreateOrderCommand("key-1", Guid.NewGuid(),
            [new CreateOrderLineItemRequest("SKU-1", 1, 10m), new CreateOrderLineItemRequest("SKU-2", 1, 5m)], "USD");
        var reservedId = Guid.NewGuid();

        _orderRepository.GetByIdempotencyKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Order?)null);
        _inventoryClient.ReserveAsync(Arg.Any<Guid>(), "SKU-1", 1, Arg.Any<CancellationToken>())
            .Returns(new InventoryReserveResult(InventoryReserveOutcome.Reserved, reservedId));
        _inventoryClient.ReserveAsync(Arg.Any<Guid>(), "SKU-2", 1, Arg.Any<CancellationToken>())
            .Returns(new InventoryReserveResult(InventoryReserveOutcome.InsufficientStock, null));

        var handler = CreateHandler();
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _inventoryClient.Received(1).ReleaseAsync(reservedId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DuplicateIdempotencyKey_IdenticalPayload_ReturnsExistingOrder_NeverCallsInventory()
    {
        var userId = Guid.NewGuid();
        var existing = Order.Create(Guid.NewGuid(), userId, "key-1",
            [new CreateOrderLineItem("SKU-1", 2, 10m, "USD", Guid.NewGuid())], "user-1", Now);

        _orderRepository.GetByIdempotencyKeyAsync("key-1", Arg.Any<CancellationToken>()).Returns(existing);

        var handler = CreateHandler();
        var result = await handler.Handle(SampleCommand(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.OrderId.Should().Be(existing.OrderId);
        await _inventoryClient.DidNotReceive().ReserveAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DuplicateIdempotencyKey_DifferentPayload_ReturnsConflict()
    {
        var existing = Order.Create(Guid.NewGuid(), Guid.NewGuid(), "key-1",
            [new CreateOrderLineItem("SKU-1", 2, 10m, "USD", Guid.NewGuid())], "user-1", Now);

        _orderRepository.GetByIdempotencyKeyAsync("key-1", Arg.Any<CancellationToken>()).Returns(existing);

        var handler = CreateHandler();
        var result = await handler.Handle(SampleCommand(Guid.NewGuid()), CancellationToken.None); // different userId

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("idempotency_conflict");
    }

    [Fact]
    public async Task Handle_ConcurrentDuplicateRequest_LosesUniqueConstraintRace_ReleasesOwnReservationsAndReplaysWinner()
    {
        var userId = Guid.NewGuid();
        var winner = Order.Create(Guid.NewGuid(), userId, "key-1",
            [new CreateOrderLineItem("SKU-1", 2, 10m, "USD", Guid.NewGuid())], "user-1", Now);
        var reservedId = Guid.NewGuid();

        _orderRepository.GetByIdempotencyKeyAsync("key-1", Arg.Any<CancellationToken>())
            .Returns((Order?)null, winner); // first check: none yet; after losing the race: the winner exists.
        _inventoryClient.ReserveAsync(Arg.Any<Guid>(), "SKU-1", 2, Arg.Any<CancellationToken>())
            .Returns(new InventoryReserveResult(InventoryReserveOutcome.Reserved, reservedId));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new DuplicateKeyException("duplicate")));

        var handler = CreateHandler();
        var result = await handler.Handle(SampleCommand(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.OrderId.Should().Be(winner.OrderId);
        await _inventoryClient.Received(1).ReleaseAsync(reservedId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).RollbackTransactionAsync(Arg.Any<CancellationToken>());
    }
}
