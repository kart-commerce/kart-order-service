using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using MediatR;

namespace KartOrderService.Application.Features.ResolveFulfillmentException;

/// <summary>ORD-12 — `api-contract.yaml`'s `POST /v1/orders/{id}/resolve-fulfillment-exception`. Admin-only (`AdminOnly` policy, enforced at the API layer).</summary>
public sealed record ResolveFulfillmentExceptionCommand(Guid OrderId, string Action, string IdempotencyKey) : IRequest<Result<OrderViewDto>>;
