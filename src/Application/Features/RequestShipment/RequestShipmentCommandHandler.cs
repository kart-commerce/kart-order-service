using System.Linq;
using Kart.Shared.Auditing;
using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Mapping;
using KartOrderService.Application.Common.Models;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.RequestShipment;

/// <summary>
/// Flow #7: durably records an admin's intent to ship a paid order (an `OrderShipmentRequested`
/// outbox event) for the eventual `kart-shipping-service` (flow #8) consumer — no status change and,
/// by design, no downstream side effect yet. Follows the identical principal-scoped-transaction /
/// compare-and-swap shape as <c>CancelOrderCommandHandler</c>.
/// </summary>
public sealed class RequestShipmentCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    ICurrentPrincipal currentPrincipal,
    TimeProvider timeProvider,
    IAuditLogWriter auditLogWriter,
    ILogger<RequestShipmentCommandHandler> logger) : IRequestHandler<RequestShipmentCommand, Result<OrderViewDto>>
{
    public async Task<Result<OrderViewDto>> Handle(RequestShipmentCommand request, CancellationToken cancellationToken)
    {
        var actingPrincipal = currentPrincipal.ActingPrincipal;
        var kind = currentPrincipal.Kind;
        var now = timeProvider.GetUtcNow();

        logger.LogInformation("Stage {Stage}: shipment request received for order {OrderId}", "RequestShipmentHandlerStarted", request.OrderId);

        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, kind, cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: shipment request rejected, order {OrderId} was not found", "RequestShipmentNotFound", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.NotFound($"Order {request.OrderId} was not found."));
        }

        var requestResult = order.RequestShipment(actingPrincipal, now);
        if (requestResult.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: order {OrderId} is not eligible for a shipment request from status '{Status}'", "RequestShipmentNotEligible", order.OrderId, order.Status);
            return Result.Failure<OrderViewDto>(requestResult.Error);
        }

        logger.LogInformation("Stage {Stage}: order {OrderId} is Paid and eligible for shipment request", "RequestShipmentEligibleBranch", order.OrderId);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: concurrent writer moved order {OrderId} during shipment request", "OrderConcurrencyConflictDetected", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.Conflict("A concurrent writer already moved this order; please retry."));
        }

        var shipmentRequestedEvent = order.Events.LastOrDefault(e => e.EventType == "OrderShipmentRequested");

        await auditLogWriter.WriteAsync(
            AuditLogEntry.Create("kart-order-service", actingPrincipal, kind, "order.shipment_requested", "Order", order.OrderId.ToString()),
            cancellationToken);

        logger.LogInformation(
            "Stage {Stage}: shipment request persisted for order {OrderId}, outbox event {OutboxEventId} (OrderShipmentRequested) enqueued",
            "RequestShipmentProcessCompleted",
            order.OrderId,
            shipmentRequestedEvent?.Id);

        return Result.Success(OrderMapper.ToDto(order));
    }
}
