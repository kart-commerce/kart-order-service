namespace KartOrderService.Application.Common.Models;

/// <summary>`api-contract.yaml`'s `OrderLineItemView` schema.</summary>
public sealed record OrderLineItemViewDto(string Sku, int Qty, MoneyDto UnitPrice);

/// <summary>`api-contract.yaml`'s `ShippingAddress` schema — same shape as the domain <see cref="Domain.Orders.ShippingAddress"/> value object. Null when the order has no address set.</summary>
public sealed record ShippingAddressDto(
    string RecipientName,
    string Line1,
    string? Line2,
    string City,
    string State,
    string PostalCode,
    string Country,
    string? Phone);

/// <summary>
/// `api-contract.yaml`'s `OrderView` schema — the shape both `POST /v1/orders`'s `202` response
/// and `GET /v1/orders/{id}` (from the Mongo read model, ORD-4) return. `Status` is serialized as
/// the enum's string name (`Created`, `Reserved`, ... — matching the OpenAPI enum exactly).
/// </summary>
public sealed record OrderViewDto(
    Guid OrderId,
    Guid UserId,
    string Status,
    IReadOnlyList<OrderLineItemViewDto> Items,
    MoneyDto TotalAmount,
    DateTimeOffset CreatedAt,
    ShippingAddressDto? ShippingAddress);

/// <summary>Flow #7 admin list row — a slim projection (no line items) of the Mongo read model, returned by `GET /v1/orders`.</summary>
public sealed record OrderSummaryDto(
    Guid OrderId,
    Guid UserId,
    string Status,
    MoneyDto TotalAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Flow #7 admin list envelope — a page of <see cref="OrderSummaryDto"/> plus the unpaged total.</summary>
public sealed record PagedOrdersDto(
    IReadOnlyList<OrderSummaryDto> Items,
    long TotalCount,
    int Page,
    int PageSize);
