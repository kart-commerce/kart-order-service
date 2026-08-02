using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Models;
using MongoDB.Driver;

namespace KartOrderService.Infrastructure.Persistence.ReadModel;

/// <summary>ORD-4: `GET /v1/orders/{id}` reads exclusively through here.</summary>
public sealed class OrderReadRepository(OrderReadDbContext readDbContext) : IOrderReadRepository
{
    public async Task<OrderViewDto?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var document = await readDbContext.Orders
            .Find(d => d.Id == orderId)
            .FirstOrDefaultAsync(cancellationToken);

        return document is null
            ? null
            : new OrderViewDto(
                document.Id,
                document.UserId,
                document.Status,
                document.Items.Select(i => new OrderLineItemViewDto(i.Sku, i.Qty, new MoneyDto(i.UnitPrice.Amount, i.UnitPrice.Currency))).ToList(),
                new MoneyDto(document.TotalAmount.Amount, document.TotalAmount.Currency),
                document.CreatedAt);
    }
}
