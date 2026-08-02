using Kart.Shared.Domain;
using MediatR;

namespace KartOrderService.Application.Features.AdvanceOnShipmentDispatched;

/// <summary>ORD-9 — consumes `ShipmentDispatched` (`orderId`, `carrier`, `trackingId`).</summary>
public sealed record ConsumeShipmentDispatchedCommand(Guid OrderId, string TrackingId) : IRequest<Result>;
