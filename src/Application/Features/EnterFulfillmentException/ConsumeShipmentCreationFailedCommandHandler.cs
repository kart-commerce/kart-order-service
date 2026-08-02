using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using MediatR;

namespace KartOrderService.Application.Features.EnterFulfillmentException;

/// <summary>ORD-11: `Paid→FulfillmentException` (ADR-0015). No published event; resolution (`ORD-12`) requires an explicit manual/ops action — see design-decisions.md's "Post-Confirmation Fulfillment Exception Handling."</summary>
public sealed class ConsumeShipmentCreationFailedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<ConsumeShipmentCreationFailedCommand, Result>
{
    public async Task<Result> Handle(ConsumeShipmentCreationFailedCommand request, CancellationToken cancellationToken)
    {
        const string actingPrincipal = SystemPrincipals.ShippingConsumer;
        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, "system", cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure(Error.NotFound($"Order {request.OrderId} was not found for ShipmentCreationFailed."));
        }

        var now = timeProvider.GetUtcNow();
        var result = order.TryEnterFulfillmentException(actingPrincipal, now);
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
