using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.EnterFulfillmentException;

/// <summary>ORD-11: `Paid→FulfillmentException` (ADR-0015). No published event; resolution (`ORD-12`) requires an explicit manual/ops action — see design-decisions.md's "Post-Confirmation Fulfillment Exception Handling."</summary>
public sealed class ConsumeShipmentCreationFailedCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<ConsumeShipmentCreationFailedCommandHandler> logger) : IRequestHandler<ConsumeShipmentCreationFailedCommand, Result>
{
    public async Task<Result> Handle(ConsumeShipmentCreationFailedCommand request, CancellationToken cancellationToken)
    {
        const string actingPrincipal = SystemPrincipals.ShippingConsumer;
        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, "system", cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: ShipmentCreationFailed received for order {OrderId} but no order was found", "ShipmentCreationFailedNoOrderFound", request.OrderId);
            return Result.Failure(Error.NotFound($"Order {request.OrderId} was not found for ShipmentCreationFailed."));
        }

        var now = timeProvider.GetUtcNow();
        var result = order.TryEnterFulfillmentException(actingPrincipal, now);
        if (result.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: order {OrderId} could not enter FulfillmentException from status '{Status}' — {Error}", "FulfillmentExceptionEntryFailed", order.OrderId, order.Status, result.Error.Message);
            return result;
        }

        // Stage 5 decision branch — this is the actual escalation trigger Flow #7's "Handle Order
        // Escalation" step exists to resolve (see ResolveFulfillmentExceptionCommandHandler).
        logger.LogInformation("Stage {Stage}: order {OrderId} escalation triggered — entering FulfillmentException", "FulfillmentExceptionEscalationTriggeredBranch", order.OrderId);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: concurrent writer moved order {OrderId} while entering FulfillmentException", "OrderConcurrencyConflictDetected", request.OrderId);
            return Result.Failure(Error.Conflict($"A concurrent writer already moved order {request.OrderId}."));
        }

        logger.LogInformation("Stage {Stage}: order {OrderId} persisted as FulfillmentException (no outbox event — internal-only transition)", "OrderPersistedFulfillmentException", order.OrderId);
        logger.LogInformation("Stage {Stage}: order {OrderId} escalation-trigger step of Order Management (Admin) completed", "OrderManagementAdminEscalationTriggerCompleted", order.OrderId);

        return Result.Success();
    }
}
