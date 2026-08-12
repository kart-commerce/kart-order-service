namespace KartOrderService.Domain.Orders;

/// <summary>
/// Flow #7 (Order Management, Admin) addition: the delivery address an admin can attach/correct on
/// an order while it has not yet shipped. Modeled as an EF Core owned value object on the `orders`
/// table itself (all columns nullable — most existing/new orders never set one), never a separate
/// aggregate. Kept deliberately flat/string-typed to match `database-design.md`'s column-per-field
/// convention rather than introducing nested owned types.
/// </summary>
public sealed record ShippingAddress(
    string RecipientName,
    string Line1,
    string? Line2,
    string City,
    string State,
    string PostalCode,
    string Country,
    string? Phone);
