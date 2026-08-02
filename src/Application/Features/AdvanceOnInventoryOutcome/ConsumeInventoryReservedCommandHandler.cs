using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using MediatR;

namespace KartOrderService.Application.Features.AdvanceOnInventoryOutcome;

/// <summary>
/// ORD-6: marks the named line item's reservation confirmed, then advances `Created→Reserved`
/// once every line item is confirmed (`Order.TryAdvanceToReserved`'s own idempotent/wait-for-more
/// logic). No published event — internal-only transition.
/// </summary>
public sealed class ConsumeInventoryReservedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<ConsumeInventoryReservedCommand, Result>
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
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        order.MarkLineItemReservationConfirmed(request.Sku, actingPrincipal, now);

        var advanceResult = order.TryAdvanceToReserved(actingPrincipal, now);
        if (advanceResult.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return advanceResult;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure(Error.Conflict($"A concurrent writer already moved order {request.OrderId}."));
        }

        return Result.Success();
    }
}
