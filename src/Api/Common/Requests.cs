using KartOrderService.Application.Common.Models;

namespace KartOrderService.Api.Common;

/// <summary>api-contract.yaml `POST /v1/orders` request body.</summary>
public sealed record CreateOrderItemRequest(string Sku, int Qty, MoneyDto UnitPrice);

public sealed record CreateOrderRequest(Guid UserId, IReadOnlyList<CreateOrderItemRequest> Items, string Currency);

/// <summary>api-contract.yaml `POST /v1/orders/{id}/resolve-fulfillment-exception` request body.</summary>
public sealed record ResolveFulfillmentExceptionRequest(string Action);
