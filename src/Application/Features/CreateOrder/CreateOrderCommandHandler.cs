using Kart.Shared.Domain;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Mapping;
using KartOrderService.Application.Common.Models;
using KartOrderService.Domain.Orders;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.CreateOrder;

/// <summary>
/// ORD-1: `IdempotencyKey` handled without a separate ledger table (ddd-model.md's contrast with
/// Payment — the key lives directly on `Order`, guarded by `idx_orders_idempotency_key`'s unique
/// constraint) plus a synchronous, per-line-item Inventory reserve fan-out (`contracts/README.md`
/// addendum #1 — Inventory's real contract reserves one `(sku, qty)` per call, not a whole order).
/// </summary>
public sealed class CreateOrderCommandHandler(
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    IInventoryClient inventoryClient,
    ICurrentPrincipal currentPrincipal,
    TimeProvider timeProvider,
    ILogger<CreateOrderCommandHandler> logger) : IRequestHandler<CreateOrderCommand, Result<OrderViewDto>>
{
    public async Task<Result<OrderViewDto>> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        var existing = await orderRepository.GetByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Stage {Stage}: idempotency key {IdempotencyKey} already has an order {OrderId} — replaying instead of reserving inventory again", "IdempotencyKeyReplayBranch", request.IdempotencyKey, existing.OrderId);
            return ReplayOrConflict(existing, request, logger);
        }

        var orderId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var actingPrincipal = currentPrincipal.ActingPrincipal;

        // design-decisions.md's "Communication Style Per Saga Dependency": the one genuinely
        // synchronous outbound edge (ADR-0009), fanned out per line item, in parallel, so the whole
        // fan-out stays inside the 2s budget regardless of item count.
        var reserveResults = await Task.WhenAll(request.Items.Select(item => ReserveLineAsync(orderId, item, cancellationToken)));

        var failed = reserveResults.Where(r => r.Result.Outcome != InventoryReserveOutcome.Reserved).ToList();
        if (failed.Count > 0)
        {
            // Compensation completeness invariant: release whichever lines DID reserve before
            // failing the request — this order will never be persisted, so nothing else will.
            await ReleaseAllAsync(reserveResults, cancellationToken);

            var unavailable = failed.Any(f => f.Result.Outcome == InventoryReserveOutcome.Unavailable);
            logger.LogWarning(
                "Stage {Stage}: order create rejected for user {UserId} — {FailedCount} of {ItemCount} line item(s) failed to reserve ({Outcome})",
                "InventoryReservationRejected",
                request.UserId,
                failed.Count,
                reserveResults.Length,
                unavailable ? "inventory_unavailable" : "insufficient_stock");

            return unavailable
                ? Result.Failure<OrderViewDto>(Error.Custom("inventory_unavailable", "Inventory's synchronous reserve call timed out or its circuit breaker is open."))
                : Result.Failure<OrderViewDto>(Error.Custom("insufficient_stock", "Insufficient stock for one or more line items — no order was created."));
        }

        logger.LogInformation("Stage {Stage}: all {ItemCount} line item(s) reserved for order {OrderId}", "InventoryReservationSucceededBranch", reserveResults.Length, orderId);

        var createItems = reserveResults
            .Select(r => new CreateOrderLineItem(r.Item.Sku, r.Item.Qty, r.Item.UnitPrice, request.Currency, r.Result.ReservationId))
            .ToList();

        var order = Order.Create(orderId, request.UserId, request.IdempotencyKey, createItems, actingPrincipal, now);

        await unitOfWork.BeginPrincipalScopedTransactionAsync(actingPrincipal, currentPrincipal.Kind, cancellationToken);
        try
        {
            orderRepository.Add(order);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Stage {Stage}: CreateOrder lost the idempotency-key insert race for {IdempotencyKey}; reloading the winner", "IdempotencyKeyInsertRaceLost", request.IdempotencyKey);

            // A concurrent request won the (idempotencyKey) unique-constraint race — this attempt's
            // order will never be persisted, so its own just-made reservations must be released too.
            await ReleaseAllAsync(reserveResults, cancellationToken);

            var winner = await orderRepository.GetByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken);
            if (winner is null)
            {
                logger.LogWarning("Stage {Stage}: idempotency key {IdempotencyKey} race left no winner to reload", "IdempotencyKeyRaceUnresolved", request.IdempotencyKey);
                return Result.Failure<OrderViewDto>(Error.Conflict("A concurrent duplicate request could not be resolved."));
            }

            return ReplayOrConflict(winner, request, logger);
        }

        var orderCreatedEvent = order.Events.Single(e => e.EventType == "OrderCreated");
        logger.LogInformation(
            "Stage {Stage}: order {OrderId} created for user {UserId}, outbox event {OutboxEventId} (OrderCreated) enqueued",
            "OrderCreateProcessCompleted",
            order.OrderId,
            order.UserId,
            orderCreatedEvent.Id);

        return Result.Success(OrderMapper.ToDto(order));
    }

    private async Task<(CreateOrderLineItemRequest Item, InventoryReserveResult Result)> ReserveLineAsync(
        Guid orderId, CreateOrderLineItemRequest item, CancellationToken cancellationToken)
    {
        var result = await inventoryClient.ReserveAsync(orderId, item.Sku, item.Qty, cancellationToken);
        return (item, result);
    }

    private Task ReleaseAllAsync(IEnumerable<(CreateOrderLineItemRequest Item, InventoryReserveResult Result)> reserved, CancellationToken cancellationToken) =>
        Task.WhenAll(reserved
            .Where(r => r.Result.Outcome == InventoryReserveOutcome.Reserved && r.Result.ReservationId.HasValue)
            .Select(r => inventoryClient.ReleaseAsync(r.Result.ReservationId!.Value, cancellationToken)));

    /// <summary>requirement-spec Open Questions resolution #3: identical replay ⇒ the original order's representation; different ⇒ `422`.</summary>
    private static Result<OrderViewDto> ReplayOrConflict(Order existing, CreateOrderCommand request, ILogger logger)
    {
        var incomingItems = request.Items.Select(i => (i.Sku, i.Qty, i.UnitPrice)).ToList();
        if (existing.MatchesRequest(request.UserId, request.Currency, incomingItems))
        {
            return Result.Success(OrderMapper.ToDto(existing));
        }

        logger.LogWarning(
            "Stage {Stage}: idempotency key {IdempotencyKey} reused with a materially different request body for order {OrderId}",
            "IdempotencyKeyConflict",
            request.IdempotencyKey,
            existing.OrderId);
        return Result.Failure<OrderViewDto>(Error.Custom("idempotency_conflict", "Idempotency-Key was reused with a materially different request body."));
    }

}
