using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using MediatR;

namespace KartOrderService.Application.Features.CompleteOrderOnDelivery;

/// <summary>
/// ORD-10: `Shipped→Delivered`, publishes `OrderDelivered` (ADR-0005). design-decisions.md's
/// ordering guard: if the order hasn't reached `Shipped` yet (or doesn't exist under this
/// `trackingId` at all — `ShipmentDispatched` genuinely hasn't been processed yet), this returns
/// `Failure` rather than silently skipping the "legal transitions only" invariant; the consumer
/// hosted service's own nack/requeue-via-retry-ladder mechanics (`Kart.Shared.Messaging`) is what
/// actually implements the bounded hold-then-DLQ behavior design-decisions.md specifies — this
/// handler only signals "not ready yet," it doesn't itself sleep/retry. Already-`Delivered` is an
/// idempotent no-op (`Order.TryAdvanceToDelivered`'s own state-guard).
/// </summary>
public sealed class ConsumeDeliveryStatusUpdatedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<ConsumeDeliveryStatusUpdatedCommand, Result>
{
    public async Task<Result> Handle(ConsumeDeliveryStatusUpdatedCommand request, CancellationToken cancellationToken)
    {
        const string actingPrincipal = SystemPrincipals.DeliveryConsumer;
        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, "system", cancellationToken);

        var order = await orderRepository.GetByTrackingIdAsync(request.TrackingId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure(Error.NotFound($"No order has recorded trackingId '{request.TrackingId}' yet."));
        }

        var now = timeProvider.GetUtcNow();
        var result = order.TryAdvanceToDelivered(actingPrincipal, now);
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
            return Result.Failure(Error.Conflict($"A concurrent writer already moved order {order.OrderId}."));
        }

        return Result.Success();
    }
}
