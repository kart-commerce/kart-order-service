using System.Linq;
using Kart.Shared.Auditing;
using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Mapping;
using KartOrderService.Application.Common.Models;
using KartOrderService.Domain.Orders;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.UpdateOrderShippingAddress;

/// <summary>
/// Flow #7: attaches/corrects an order's shipping address, following the identical
/// principal-scoped-transaction / compare-and-swap shape as <c>CancelOrderCommandHandler</c>. The
/// address edit is legal only while the order has not yet shipped — the domain method returns a
/// <c>Conflict</c> otherwise.
/// </summary>
public sealed class UpdateOrderShippingAddressCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    ICurrentPrincipal currentPrincipal,
    TimeProvider timeProvider,
    IAuditLogWriter auditLogWriter,
    ILogger<UpdateOrderShippingAddressCommandHandler> logger) : IRequestHandler<UpdateOrderShippingAddressCommand, Result<OrderViewDto>>
{
    public async Task<Result<OrderViewDto>> Handle(UpdateOrderShippingAddressCommand request, CancellationToken cancellationToken)
    {
        var actingPrincipal = currentPrincipal.ActingPrincipal;
        var kind = currentPrincipal.Kind;
        var now = timeProvider.GetUtcNow();

        logger.LogInformation("Stage {Stage}: shipping-address update requested for order {OrderId}", "UpdateOrderShippingAddressHandlerStarted", request.OrderId);

        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, kind, cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: shipping-address update rejected, order {OrderId} was not found", "UpdateOrderShippingAddressNotFound", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.NotFound($"Order {request.OrderId} was not found."));
        }

        var address = new ShippingAddress(request.RecipientName, request.Line1, request.Line2, request.City, request.State, request.PostalCode, request.Country, request.Phone);
        var updateResult = order.UpdateShippingAddress(address, actingPrincipal, now);
        if (updateResult.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: order {OrderId} shipping address cannot be changed from status '{Status}'", "UpdateOrderShippingAddressNotEligible", order.OrderId, order.Status);
            return Result.Failure<OrderViewDto>(updateResult.Error);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: concurrent writer moved order {OrderId} during shipping-address update", "OrderConcurrencyConflictDetected", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.Conflict("A concurrent writer already moved this order; please retry."));
        }

        logger.LogInformation("Stage {Stage}: shipping address persisted for order {OrderId}", "OrderPersistedToDatabase", order.OrderId);

        var addressUpdatedEvent = order.Events.LastOrDefault(e => e.EventType == "OrderShippingAddressUpdated");
        logger.LogInformation("Stage {Stage}: outbox event {OutboxEventId} (OrderShippingAddressUpdated) enqueued for order {OrderId}", "OrderShippingAddressUpdatedOutboxEventSaved", addressUpdatedEvent?.Id, order.OrderId);

        await auditLogWriter.WriteAsync(
            AuditLogEntry.Create("kart-order-service", actingPrincipal, kind, "order.shipping_address.updated", "Order", order.OrderId.ToString()),
            cancellationToken);

        logger.LogInformation("Stage {Stage}: shipping-address update process completed for order {OrderId}", "UpdateOrderShippingAddressProcessCompleted", order.OrderId);

        return Result.Success(OrderMapper.ToDto(order));
    }
}
