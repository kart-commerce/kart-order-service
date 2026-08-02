using Kart.Shared.Domain;
using KartOrderService.Application.Common.Compensation;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using KartOrderService.Domain.Orders;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.CompensateOnPaymentFailed;

/// <summary>
/// ORD-8: pre-confirmation compensation — release Inventory, then `→Cancelled`
/// (edge-cases.md "Payment-Success/Shipping-Failure Compensation Ordering"'s reverse-order
/// mechanism). Shares <see cref="InventoryReleaseCompensator"/> with ORD-13 per tickets.md's own
/// grouping note. Guarded to only apply from `Created`/`Reserved` — a `PaymentFailed` arriving
/// after the order has already reached `Paid` (a genuine anomaly, since Payment's own
/// one-`PaymentIntent`-per-order invariant should preclude it) must never cancel an order money
/// has already been captured against; that is a no-op here, not a compensation trigger.
/// </summary>
public sealed class ConsumePaymentFailedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    InventoryReleaseCompensator compensator,
    TimeProvider timeProvider,
    ILogger<ConsumePaymentFailedCommandHandler> logger) : IRequestHandler<ConsumePaymentFailedCommand, Result>
{
    public async Task<Result> Handle(ConsumePaymentFailedCommand request, CancellationToken cancellationToken)
    {
        const string actingPrincipal = SystemPrincipals.PaymentConsumer;
        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, "system", cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure(Error.NotFound($"Order {request.OrderId} was not found for PaymentFailed."));
        }

        if (order.Status is not (OrderStatus.Created or OrderStatus.Reserved))
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("PaymentFailed received for order {OrderId} already in status {Status} — treated as a stale/out-of-order signal, no-op.", request.OrderId, order.Status);
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        order.RecordCompensationTriggered(request.Reason, actingPrincipal, now);
        await compensator.ReleaseAllAsync(order, cancellationToken);

        var cancelResult = order.TryCancel(request.Reason, actingPrincipal, now);
        if (cancelResult.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return cancelResult;
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
