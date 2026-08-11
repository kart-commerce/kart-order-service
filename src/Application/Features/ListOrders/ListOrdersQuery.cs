using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using KartOrderService.Domain.Orders;
using MediatR;

namespace KartOrderService.Application.Features.ListOrders;

/// <summary>Flow #7 — `api-contract.yaml`'s `GET /v1/orders`. Admin-only order list/search, served from the Mongo read model.</summary>
public sealed record ListOrdersQuery(
    OrderStatus? Status,
    Guid? UserId,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    int Page,
    int PageSize) : IRequest<Result<PagedOrdersDto>>;
