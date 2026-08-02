using Kart.Shared.Domain;
using KartOrderService.Application.Common.Compensation;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using KartOrderService.Domain.Orders;
using MediatR;

namespace KartOrderService.Application.Features.ReactToChargeback;

/// <summary>
/// ORD-13: direct `→Refunded` from any `Paid`+ state including `FulfillmentException` (ADR-0012) —
/// conditional idempotent Inventory release (shares <see cref="InventoryReleaseCompensator"/> with
/// ORD-8 per tickets.md's grouping note), never a Payment refund call (the bank already reversed
/// the charge externally). Idempotent no-op if already `Refunded`.
/// </summary>
public sealed class ConsumeChargebackReceivedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    InventoryReleaseCompensator compensator,
    TimeProvider timeProvider) : IRequestHandler<ConsumeChargebackReceivedCommand, Result>
{
    public async Task<Result> Handle(ConsumeChargebackReceivedCommand request, CancellationToken cancellationToken)
    {
        const string actingPrincipal = SystemPrincipals.ChargebackConsumer;
        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, "system", cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure(Error.NotFound($"Order {request.OrderId} was not found for ChargebackReceived."));
        }

        if (order.Status == OrderStatus.Refunded)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Success(); // idempotent no-op — never a second Inventory release attempt or a second Refunded transition.
        }

        var now = timeProvider.GetUtcNow();
        order.RecordCompensationTriggered($"chargeback:{request.ChargebackId}", actingPrincipal, now);
        await compensator.ReleaseAllAsync(order, cancellationToken);

        var result = order.TryReactToChargeback(actingPrincipal, now);
        if (result.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return result;
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
