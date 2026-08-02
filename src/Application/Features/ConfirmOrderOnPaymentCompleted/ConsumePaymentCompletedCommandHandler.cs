using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using MediatR;

namespace KartOrderService.Application.Features.ConfirmOrderOnPaymentCompleted;

/// <summary>ORD-7: `Reserved→Paid`, publishes `OrderConfirmed` (ADR-0002 — as soon as `PaymentCompleted` is received, not gated on shipment creation). Captures `PaymentIntentId` for the later refund path (`contracts/README.md` addendum #3).</summary>
public sealed class ConsumePaymentCompletedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<ConsumePaymentCompletedCommand, Result>
{
    public async Task<Result> Handle(ConsumePaymentCompletedCommand request, CancellationToken cancellationToken)
    {
        const string actingPrincipal = SystemPrincipals.PaymentConsumer;
        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, "system", cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure(Error.NotFound($"Order {request.OrderId} was not found for PaymentCompleted."));
        }

        var now = timeProvider.GetUtcNow();
        var result = order.TryAdvanceToPaid(request.PaymentIntentId, actingPrincipal, now);
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
