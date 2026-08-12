using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using MediatR;

namespace KartOrderService.Application.Features.RequestShipment;

/// <summary>Flow #7 — `api-contract.yaml`'s `POST /v1/orders/{id}/request-shipment`. Admin-only; records the admin's intent to ship a paid order.</summary>
public sealed record RequestShipmentCommand(Guid OrderId, string IdempotencyKey) : IRequest<Result<OrderViewDto>>;
