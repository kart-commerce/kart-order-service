using Kart.Shared.Domain;
using MediatR;

namespace KartOrderService.Application.Features.AdvanceOnInventoryOutcome;

/// <summary>ORD-6 — consumes `InventoryReserved` (`orderId`, `sku`, `qty`).</summary>
public sealed record ConsumeInventoryReservedCommand(Guid OrderId, string Sku) : IRequest<Result>;
