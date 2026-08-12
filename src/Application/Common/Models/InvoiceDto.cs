namespace KartOrderService.Application.Common.Models;

/// <summary>
/// Flow #7 admin invoice view, returned by `GET /v1/orders/{id}/invoice`. Derived entirely from the
/// order's current read-model state — nothing here is separately stored. <see cref="InvoiceNumber"/>
/// is a deterministic function of the order id; <see cref="IssuedAt"/> is the moment the invoice was
/// viewed, not a persisted timestamp.
///
/// <para><see cref="Subtotal"/> and <see cref="Total"/> are intentionally equal: kart-order-service
/// has no separate tax or shipping-fee line, so there is nothing to add on top of the order total.
/// This is deliberate, not a missing calculation — no fake tax logic is invented here.</para>
/// </summary>
public sealed record InvoiceDto(
    string InvoiceNumber,
    Guid OrderId,
    Guid UserId,
    string Status,
    IReadOnlyList<OrderLineItemViewDto> Items,
    MoneyDto Subtotal,
    MoneyDto Total,
    ShippingAddressDto? ShippingAddress,
    DateTimeOffset OrderCreatedAt,
    DateTimeOffset IssuedAt);
