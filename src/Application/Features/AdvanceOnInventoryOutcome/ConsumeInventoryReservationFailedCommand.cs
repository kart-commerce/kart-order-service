using Kart.Shared.Domain;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.AdvanceOnInventoryOutcome;

/// <summary>ORD-6 — consumes `InventoryReservationFailed` (`orderId`, `sku`).</summary>
public sealed record ConsumeInventoryReservationFailedCommand(Guid OrderId, string Sku) : IRequest<Result>;

/// <summary>
/// event-contract.md: "this event is the async saga-advancement signal for [the synchronous
/// reserve] call (ADR-0009), not a separate compensation trigger" — no order persists past a
/// failed synchronous reserve call, so there is nothing here to compensate; a genuine no-op, logged
/// for traceability only.
/// </summary>
public sealed class ConsumeInventoryReservationFailedCommandHandler(ILogger<ConsumeInventoryReservationFailedCommandHandler> logger)
    : IRequestHandler<ConsumeInventoryReservationFailedCommand, Result>
{
    public Task<Result> Handle(ConsumeInventoryReservationFailedCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Stage {Stage}: InventoryReservationFailed received for order {OrderId}/sku {Sku} — no-op, no order persists past a failed synchronous reserve call",
            "InventoryReservationFailedNoOpBranch",
            request.OrderId,
            request.Sku);
        return Task.FromResult(Result.Success());
    }
}
