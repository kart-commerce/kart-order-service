namespace KartOrderService.Application.Common.Models;

/// <summary>`api-contract.yaml`'s `OrderLineItemView` schema.</summary>
public sealed record OrderLineItemViewDto(string Sku, int Qty, MoneyDto UnitPrice);

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
    DateTimeOffset CreatedAt);
