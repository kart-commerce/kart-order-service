using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using MediatR;

namespace KartOrderService.Application.Features.AdvanceOnShipmentDispatched;

/// <summary>ORD-9: `Paid→Shipped`, informational only (ADR-0002) — does not gate `OrderConfirmed`, which already published on `PaymentCompleted`.</summary>
public sealed class ConsumeShipmentDispatchedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<ConsumeShipmentDispatchedCommand, Result>
{
    public async Task<Result> Handle(ConsumeShipmentDispatchedCommand request, CancellationToken cancellationToken)
    {
        const string actingPrincipal = SystemPrincipals.ShippingConsumer;
        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, "system", cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure(Error.NotFound($"Order {request.OrderId} was not found for ShipmentDispatched."));
        }

        var now = timeProvider.GetUtcNow();
        var result = order.TryAdvanceToShipped(request.TrackingId, actingPrincipal, now);
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
