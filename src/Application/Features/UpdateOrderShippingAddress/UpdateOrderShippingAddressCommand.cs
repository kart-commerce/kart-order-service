using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using MediatR;

namespace KartOrderService.Application.Features.UpdateOrderShippingAddress;

/// <summary>Flow #7 — `api-contract.yaml`'s `PATCH /v1/orders/{id}/shipping-address`. Admin-only; legal only while the order has not yet shipped.</summary>
public sealed record UpdateOrderShippingAddressCommand(
    Guid OrderId,
    string RecipientName,
    string Line1,
    string? Line2,
    string City,
    string State,
    string PostalCode,
    string Country,
    string? Phone,
    string IdempotencyKey) : IRequest<Result<OrderViewDto>>;
