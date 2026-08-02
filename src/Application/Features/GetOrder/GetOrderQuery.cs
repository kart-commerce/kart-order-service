using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using MediatR;

namespace KartOrderService.Application.Features.GetOrder;

/// <summary>ORD-4 — `api-contract.yaml`'s `GET /v1/orders/{id}`.</summary>
public sealed record GetOrderQuery(Guid OrderId) : IRequest<Result<OrderViewDto>>;
