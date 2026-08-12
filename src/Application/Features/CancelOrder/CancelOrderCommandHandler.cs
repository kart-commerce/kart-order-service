using Kart.Shared.Auditing;
using Kart.Shared.Domain;
using KartOrderService.Application.Common.Compensation;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Mapping;
using KartOrderService.Application.Common.Models;
using KartOrderService.Domain.Orders;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.CancelOrder;

/// <summary>
/// ORD-5: routes through the identical compare-and-swap state machine every other trigger uses —
/// "single writer, no separate cancel code path" (edge-cases.md's "Client Cancel Request Racing an
/// In-Flight Saga" decision). Legal only pre-`Shipped`; `409` once `Shipped` or later. The
/// principal-scoped transaction begins before the aggregate is loaded so RLS gates the read too —
/// a customer's cancel call against another user's order sees `NotFound`, not `Conflict`,
/// indistinguishable from the order simply not existing.
/// </summary>
public sealed class CancelOrderCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    InventoryReleaseCompensator compensator,
    ICurrentPrincipal currentPrincipal,
    TimeProvider timeProvider,
    IAuditLogWriter auditLogWriter,
    ILogger<CancelOrderCommandHandler> logger) : IRequestHandler<CancelOrderCommand, Result<OrderViewDto>>
{
    public async Task<Result<OrderViewDto>> Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        var actingPrincipal = currentPrincipal.ActingPrincipal;
        var kind = currentPrincipal.Kind;
        var now = timeProvider.GetUtcNow();
        var reason = request.Reason ?? "client_cancel";

        logger.LogInformation("Stage {Stage}: cancel requested for order {OrderId}", "CancelOrderHandlerStarted", request.OrderId);

        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, kind, cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure<OrderViewDto>(Error.NotFound($"Order {request.OrderId} was not found."));
        }

        if (order.Status == OrderStatus.Cancelled)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Success(OrderMapper.ToDto(order)); // idempotent no-op — nothing to save.
        }

        if (!OrderStatusTransitions.IsLegalTransition(order.Status, OrderStatus.Cancelled))
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure<OrderViewDto>(Error.Conflict($"Order {request.OrderId} is already '{order.Status}' — cancellation is illegal from this state; use the returns/refund flow instead."));
        }

        order.RecordCompensationTriggered(reason, actingPrincipal, now);
        await compensator.ReleaseAllAsync(order, cancellationToken);

        var cancelResult = order.TryCancel(reason, actingPrincipal, now);
        if (cancelResult.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            return Result.Failure<OrderViewDto>(cancelResult.Error);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: concurrent writer moved order {OrderId} during cancel", "OrderConcurrencyConflictDetected", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.Conflict("A concurrent writer already moved this order; please retry."));
        }

        logger.LogInformation("Stage {Stage}: order {OrderId} cancelled and committed", "OrderPersistedToDatabase", order.OrderId);
        logger.LogInformation("Stage {Stage}: OrderCancelled outbox event saved for order {OrderId}", "OrderCancelledOutboxEventSaved", order.OrderId);

        await auditLogWriter.WriteAsync(
            AuditLogEntry.Create("kart-order-service", actingPrincipal, kind, "order.cancelled", "Order", order.OrderId.ToString()),
            cancellationToken);

        return Result.Success(OrderMapper.ToDto(order));
    }

}
