using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.AdvanceOnInventoryOutcome;

/// <summary>
/// ORD-6: marks the named line item's reservation confirmed, then advances `Created→Reserved`
/// once every line item is confirmed (`Order.TryAdvanceToReserved`'s own idempotent/wait-for-more
/// logic). No published event — internal-only transition.
/// </summary>
public sealed class ConsumeInventoryReservedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<ConsumeInventoryReservedCommandHandler> logger) : IRequestHandler<ConsumeInventoryReservedCommand, Result>
{
    public async Task<Result> Handle(ConsumeInventoryReservedCommand request, CancellationToken cancellationToken)
    {
        const string actingPrincipal = SystemPrincipals.InventoryConsumer;
        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, "system", cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            // No order persists for a synchronous reserve call that never committed — nothing to advance.
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: InventoryReserved received for order {OrderId}/sku {Sku} but no order was found — no-op", "InventoryReservedNoOrderFound", request.OrderId, request.Sku);
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        order.MarkLineItemReservationConfirmed(request.Sku, actingPrincipal, now);

        var advanceResult = order.TryAdvanceToReserved(actingPrincipal, now);
        if (advanceResult.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: order {OrderId} could not advance to Reserved after sku {Sku} confirmed — {Error}", "OrderAdvanceToReservedFailed", request.OrderId, request.Sku, advanceResult.Error.Message);
            return advanceResult;
        }

        var stillWaiting = order.Status != Domain.Orders.OrderStatus.Reserved;
        logger.LogInformation(
            stillWaiting
                ? "Stage {Stage}: sku {Sku} confirmed for order {OrderId}; still waiting on other line item(s)"
                : "Stage {Stage}: sku {Sku} confirmed for order {OrderId}; every line item now reserved",
            stillWaiting ? "OrderReservationAwaitingMoreLineItemsBranch" : "OrderReservationAllLineItemsConfirmedBranch",
            request.Sku,
            request.OrderId);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: concurrent writer moved order {OrderId} while confirming sku {Sku}", "OrderConcurrencyConflictDetected", request.OrderId, request.Sku);
            return Result.Failure(Error.Conflict($"A concurrent writer already moved order {request.OrderId}."));
        }

        if (!stillWaiting)
        {
            logger.LogInformation("Stage {Stage}: order {OrderId} persisted as Reserved (Created→Reserved, no outbox event), inventory-reserved step of the shopping journey completed", "NormalShoppingPurchaseJourneyInventoryReservedCompleted", request.OrderId);
        }

        return Result.Success();
    }
}
