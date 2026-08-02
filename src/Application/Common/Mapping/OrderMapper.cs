using KartOrderService.Application.Common.Models;
using KartOrderService.Domain.Orders;

namespace KartOrderService.Application.Common.Mapping;

/// <summary>coding-standards.md's DRY rule of thumb ("duplication across two call sites is not yet a violation worth fixing — wait for a third genuinely identical case"): extracted once `CreateOrder`/`CancelOrder`/`ResolveFulfillmentException` all needed the identical `Order → OrderViewDto` mapping.</summary>
public static class OrderMapper
{
    public static OrderViewDto ToDto(Order order) => new(
        order.OrderId,
        order.UserId,
        order.Status.ToString(),
        order.LineItems.Select(li => new OrderLineItemViewDto(li.Sku, li.Qty, new MoneyDto(li.UnitPrice, li.Currency))).ToList(),
        new MoneyDto(order.TotalAmount, order.Currency),
        order.CreatedAt);
}
