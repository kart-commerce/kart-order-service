using Kart.Shared.Domain;
using MediatR;

namespace KartOrderService.Application.Features.EnterFulfillmentException;

/// <summary>ORD-11 — consumes `ShipmentCreationFailed` (`orderId`, `reason`, ADR-0015).</summary>
public sealed record ConsumeShipmentCreationFailedCommand(Guid OrderId, string Reason) : IRequest<Result>;
