using System.Linq;
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

namespace KartOrderService.Application.Features.ResolveFulfillmentException;

/// <summary>
/// ORD-12: `retry` ⇒ `FulfillmentException→Paid`, republishes `OrderConfirmed`. `cancel` ⇒
/// conditional idempotent Inventory release, then a synchronous Payment refund call — only once
/// that call succeeds does the order transition to `Cancelled` (design-decisions.md's
/// "Post-Confirmation Fulfillment Exception Handling"). The Inventory/Payment calls happen before
/// the write transaction opens (mirrors `CreateOrder`'s own shape) so a slow external call never
/// holds a database transaction open; Admin's `"service"`-kind principal bypasses RLS regardless
/// of when the transaction starts, so loading the order read-only first costs nothing here.
/// </summary>
public sealed class ResolveFulfillmentExceptionCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    InventoryReleaseCompensator compensator,
    IPaymentClient paymentClient,
    ICurrentPrincipal currentPrincipal,
    TimeProvider timeProvider,
    IAuditLogWriter auditLogWriter,
    ILogger<ResolveFulfillmentExceptionCommandHandler> logger) : IRequestHandler<ResolveFulfillmentExceptionCommand, Result<OrderViewDto>>
{
    public async Task<Result<OrderViewDto>> Handle(ResolveFulfillmentExceptionCommand request, CancellationToken cancellationToken)
    {
        var actingPrincipal = currentPrincipal.ActingPrincipal;
        var kind = currentPrincipal.Kind;
        var now = timeProvider.GetUtcNow();

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Stage {Stage}: fulfillment-exception resolve rejected, order {OrderId} was not found", "ResolveFulfillmentExceptionNotFound", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.NotFound($"Order {request.OrderId} was not found."));
        }

        if (order.Status != OrderStatus.FulfillmentException)
        {
            logger.LogWarning("Stage {Stage}: order {OrderId} is not in FulfillmentException (status '{Status}') — nothing to resolve", "ResolveFulfillmentExceptionNotEscalated", order.OrderId, order.Status);
            return Result.Failure<OrderViewDto>(Error.Conflict($"Order {request.OrderId} is not currently in FulfillmentException — nothing to resolve."));
        }

        // Stage 5 decision branch — the admin's chosen resolution for a triggered fulfillment
        // escalation: retry (retains the order, republishes OrderConfirmed) vs. cancel
        // (compensates Inventory + refunds Payment, terminal Cancelled).
        logger.LogInformation(
            "Stage {Stage}: order {OrderId} escalation (FulfillmentException) resolved via '{Action}'",
            "FulfillmentExceptionEscalationResolvedBranch",
            order.OrderId,
            request.Action);

        if (request.Action == "retry")
        {
            var retryResult = order.TryRetryFromFulfillmentException(actingPrincipal, now);
            if (retryResult.IsFailure)
            {
                logger.LogWarning("Stage {Stage}: order {OrderId} retry-from-escalation failed — {Error}", "ResolveFulfillmentExceptionRetryFailed", order.OrderId, retryResult.Error.Message);
                return Result.Failure<OrderViewDto>(retryResult.Error);
            }
        }
        else
        {
            if (order.PaymentIntentId is null)
            {
                logger.LogWarning("Stage {Stage}: order {OrderId} has no captured PaymentIntentId — cannot issue a refund", "ResolveFulfillmentExceptionNoPaymentIntent", order.OrderId);
                return Result.Failure<OrderViewDto>(Error.Conflict($"Order {request.OrderId} has no captured PaymentIntentId — cannot issue a refund."));
            }

            order.RecordCompensationTriggered("fulfillment_exception_cancel", actingPrincipal, now);
            await compensator.ReleaseAllAsync(order, cancellationToken);

            var refundIdempotencyKey = $"{order.OrderId}:{order.PaymentIntentId}:compensation-refund"; // kart-payment-service/architecture.md's documented derivation
            var refundResult = await paymentClient.RefundAsync(order.PaymentIntentId.Value, order.TotalAmount, order.Currency, refundIdempotencyKey, cancellationToken);

            if (refundResult.Outcome != PaymentRefundOutcome.Accepted)
            {
                logger.LogWarning("Stage {Stage}: order {OrderId} cancel-via-escalation refund did not succeed; order remains in FulfillmentException", "ResolveFulfillmentExceptionRefundFailed", order.OrderId);
                return Result.Failure<OrderViewDto>(Error.Custom("refund_failed", "The Payment refund call did not succeed; the order remains in FulfillmentException."));
            }

            var cancelResult = order.TryCancel("fulfillment_exception_cancel", actingPrincipal, now);
            if (cancelResult.IsFailure)
            {
                logger.LogWarning("Stage {Stage}: order {OrderId} cancel-via-escalation failed after a successful refund — {Error}", "ResolveFulfillmentExceptionCancelFailed", order.OrderId, cancelResult.Error.Message);
                return Result.Failure<OrderViewDto>(cancelResult.Error);
            }
        }

        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, kind, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: concurrent writer moved order {OrderId} during escalation resolution", "OrderConcurrencyConflictDetected", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.Conflict("A concurrent writer already moved this order; please retry."));
        }

        var outboxEventType = request.Action == "retry" ? "OrderConfirmed" : "OrderCancelled";
        var outboxEvent = order.Events.LastOrDefault(e => e.EventType == outboxEventType);
        logger.LogInformation(
            "Stage {Stage}: order {OrderId} persisted, outbox event {OutboxEventId} ({EventType}) enqueued",
            "OrderPersistedOutboxEventEnqueued",
            order.OrderId,
            outboxEvent?.Id,
            outboxEventType);

        await auditLogWriter.WriteAsync(
            AuditLogEntry.Create("kart-order-service", actingPrincipal, kind, $"order.fulfillment_exception.{request.Action}", "Order", order.OrderId.ToString()),
            cancellationToken);

        logger.LogInformation("Stage {Stage}: escalation resolution ('{Action}') process completed for order {OrderId}", "ResolveFulfillmentExceptionProcessCompleted", request.Action, order.OrderId);

        return Result.Success(OrderMapper.ToDto(order));
    }
}
