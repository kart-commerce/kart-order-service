using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Models;
using KartOrderService.Infrastructure.Persistence.ReadModel.Documents;
using MongoDB.Driver;

namespace KartOrderService.Infrastructure.Persistence.ReadModel;

/// <summary>ORD-4: `GET /v1/orders/{id}` reads exclusively through here; Flow #7's admin list/search added alongside.</summary>
public sealed class OrderReadRepository(OrderReadDbContext readDbContext) : IOrderReadRepository
{
    public async Task<OrderViewDto?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var document = await readDbContext.Orders
            .Find(d => d.Id == orderId)
            .FirstOrDefaultAsync(cancellationToken);

        return document is null ? null : ToViewDto(document);
    }

    public async Task<(IReadOnlyList<OrderSummaryDto> Items, long TotalCount)> SearchAsync(OrderSearchFilter filter, CancellationToken cancellationToken)
    {
        var builder = Builders<OrderReadDocument>.Filter;
        var mongoFilter = builder.Empty;

        if (filter.Status is not null)
        {
            mongoFilter &= builder.Eq(d => d.Status, filter.Status.Value.ToString());
        }

        if (filter.UserId is not null)
        {
            mongoFilter &= builder.Eq(d => d.UserId, filter.UserId.Value);
        }

        if (filter.CreatedFrom is not null)
        {
            mongoFilter &= builder.Gte(d => d.CreatedAt, filter.CreatedFrom.Value);
        }

        if (filter.CreatedTo is not null)
        {
            mongoFilter &= builder.Lte(d => d.CreatedAt, filter.CreatedTo.Value);
        }

        var totalCount = await readDbContext.Orders.CountDocumentsAsync(mongoFilter, cancellationToken: cancellationToken);

        var documents = await readDbContext.Orders
            .Find(mongoFilter)
            .Sort(Builders<OrderReadDocument>.Sort.Descending(d => d.CreatedAt))
            .Skip((filter.Page - 1) * filter.PageSize)
            .Limit(filter.PageSize)
            .ToListAsync(cancellationToken);

        var items = documents
            .Select(d => new OrderSummaryDto(
                d.Id,
                d.UserId,
                d.Status,
                new MoneyDto(d.TotalAmount.Amount, d.TotalAmount.Currency),
                d.CreatedAt,
                d.UpdatedAt))
            .ToList();

        return (items, totalCount);
    }

    private static OrderViewDto ToViewDto(OrderReadDocument document) => new(
        document.Id,
        document.UserId,
        document.Status,
        document.Items.Select(i => new OrderLineItemViewDto(i.Sku, i.Qty, new MoneyDto(i.UnitPrice.Amount, i.UnitPrice.Currency))).ToList(),
        new MoneyDto(document.TotalAmount.Amount, document.TotalAmount.Currency),
        document.CreatedAt,
        document.ShippingAddress is null
            ? null
            : new ShippingAddressDto(
                document.ShippingAddress.RecipientName,
                document.ShippingAddress.Line1,
                document.ShippingAddress.Line2,
                document.ShippingAddress.City,
                document.ShippingAddress.State,
                document.ShippingAddress.PostalCode,
                document.ShippingAddress.Country,
                document.ShippingAddress.Phone));
}
