using KartOrderService.Application.Common.Models;

namespace KartOrderService.Api.Common;

/// <summary>api-contract.yaml `POST /v1/orders` request body.</summary>
public sealed record CreateOrderItemRequest(string Sku, int Qty, MoneyDto UnitPrice);

public sealed record CreateOrderRequest(Guid UserId, IReadOnlyList<CreateOrderItemRequest> Items, string Currency);

/// <summary>api-contract.yaml `POST /v1/orders/{id}/resolve-fulfillment-exception` request body.</summary>
public sealed record ResolveFulfillmentExceptionRequest(string Action);

/// <summary>Flow #7: api-contract.yaml `POST /v1/orders/{id}/cancel` optional request body — a caller may omit the body entirely.</summary>
public sealed record CancelOrderRequest(string? Reason);

/// <summary>Flow #7: api-contract.yaml `PATCH /v1/orders/{id}/shipping-address` request body.</summary>
public sealed record UpdateShippingAddressRequest(
    string RecipientName,
    string Line1,
    string? Line2,
    string City,
    string State,
    string PostalCode,
    string Country,
    string? Phone);

/// <summary>Flow #7: api-contract.yaml `PATCH /v1/orders/{id}/status` request body.</summary>
public sealed record AdminUpdateOrderStatusRequest(string TargetStatus, string Reason);
