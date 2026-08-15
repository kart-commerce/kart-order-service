using System.Linq;
using Kart.Shared.Auditing;
using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Mapping;
using KartOrderService.Application.Common.Models;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.AdminUpdateOrderStatus;

/// <summary>
/// Flow #7: an admin-initiated, audited manual status advance (ops-recovery for a stalled saga),
/// following the identical principal-scoped-transaction / compare-and-swap shape as
/// <c>CancelOrderCommandHandler</c>. Target-status policy is enforced by the validator; the domain
/// method re-validates transition legality and returns a <c>Conflict</c> for an illegal move.
/// </summary>
public sealed class AdminUpdateOrderStatusCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    ICurrentPrincipal currentPrincipal,
    TimeProvider timeProvider,
    IAuditLogWriter auditLogWriter,
    ILogger<AdminUpdateOrderStatusCommandHandler> logger) : IRequestHandler<AdminUpdateOrderStatusCommand, Result<OrderViewDto>>
{
    public async Task<Result<OrderViewDto>> Handle(AdminUpdateOrderStatusCommand request, CancellationToken cancellationToken)
    {
        var actingPrincipal = currentPrincipal.ActingPrincipal;
        var kind = currentPrincipal.Kind;
        var now = timeProvider.GetUtcNow();

        logger.LogInformation("Stage {Stage}: admin status update requested for order {OrderId} -> {TargetStatus}", "AdminUpdateOrderStatusHandlerStarted", request.OrderId, request.TargetStatus);

        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, kind, cancellationToken);

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: admin status update rejected, order {OrderId} was not found", "AdminUpdateOrderStatusNotFound", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.NotFound($"Order {request.OrderId} was not found."));
        }

        var alreadyAtTarget = order.Status == request.TargetStatus;
        logger.LogInformation(
            alreadyAtTarget ? "Stage {Stage}: order {OrderId} already '{TargetStatus}' — idempotent no-op" : "Stage {Stage}: order {OrderId} admin-advancing '{Status}' -> '{TargetStatus}'",
            alreadyAtTarget ? "AdminUpdateOrderStatusNoOpBranch" : "AdminUpdateOrderStatusAdvanceBranch",
            order.OrderId,
            alreadyAtTarget ? request.TargetStatus : order.Status,
            request.TargetStatus);

        var advanceResult = order.AdminAdvanceStatus(request.TargetStatus, request.Reason, actingPrincipal, now);
        if (advanceResult.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: order {OrderId} cannot admin-advance to '{TargetStatus}' — {Error}", "AdminUpdateOrderStatusIllegalTransition", order.OrderId, request.TargetStatus, advanceResult.Error.Message);
            return Result.Failure<OrderViewDto>(advanceResult.Error);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: concurrent writer moved order {OrderId} during admin status update", "OrderConcurrencyConflictDetected", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.Conflict("A concurrent writer already moved this order; please retry."));
        }

        var statusChangedEvent = order.Events.LastOrDefault(e => e.EventType == "OrderStatusChangedByAdmin");

        await auditLogWriter.WriteAsync(
            AuditLogEntry.Create("kart-order-service", actingPrincipal, kind, "order.status.admin_updated", "Order", order.OrderId.ToString()),
            cancellationToken);

        logger.LogInformation(
            "Stage {Stage}: order {OrderId} status advanced to {TargetStatus}, outbox event {OutboxEventId} (OrderStatusChangedByAdmin) enqueued",
            "AdminUpdateOrderStatusProcessCompleted",
            order.OrderId,
            request.TargetStatus,
            statusChangedEvent?.Id);

        return Result.Success(OrderMapper.ToDto(order));
    }
}
