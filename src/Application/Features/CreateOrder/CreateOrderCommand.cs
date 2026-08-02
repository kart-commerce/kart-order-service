using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using MediatR;

namespace KartOrderService.Application.Features.CreateOrder;

public sealed record CreateOrderLineItemRequest(string Sku, int Qty, decimal UnitPrice);

/// <summary>ORD-1 — `api-contract.yaml`'s `POST /v1/orders`.</summary>
public sealed record CreateOrderCommand(
    string IdempotencyKey,
    Guid UserId,
    IReadOnlyList<CreateOrderLineItemRequest> Items,
    string Currency) : IRequest<Result<OrderViewDto>>;
