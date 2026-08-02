using Kart.Shared.Domain;
using MediatR;

namespace KartOrderService.Application.Features.CompleteOrderOnDelivery;

/// <summary>ORD-10 — consumes `DeliveryStatusUpdated`'s terminal value only (`trackingId`, `status`); the consumer hosted service filters for the terminal status before ever dispatching this command.</summary>
public sealed record ConsumeDeliveryStatusUpdatedCommand(string TrackingId) : IRequest<Result>;
