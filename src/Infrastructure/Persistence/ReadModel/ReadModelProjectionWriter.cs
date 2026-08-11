using System.Text.Json;
using System.Text.Json.Serialization;
using KartOrderService.Domain.Orders;
using KartOrderService.Infrastructure.Persistence.ReadModel.Documents;
using MongoDB.Driver;

namespace KartOrderService.Infrastructure.Persistence.ReadModel;

/// <summary>
/// ORD-3: idempotent upserts only — "the read model is always rebuildable from the write model +
/// event log; never write to a read model outside a projection consumer" (ddd-cqrs-standards.md).
/// The `OrderCreated` row seeds the full document (items/total/userId, from its own payload — the
/// only event carrying that data); every other transition row only ever advances `status`/`updatedAt`.
/// </summary>
public sealed class ReadModelProjectionWriter(OrderReadDbContext readDbContext)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task ApplyAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        if (orderEvent.EventType == "OrderCreated" && orderEvent.Payload is not null)
        {
            await ApplyOrderCreatedAsync(orderEvent, cancellationToken);
            return;
        }

        var filter = Builders<OrderReadDocument>.Filter.Eq(d => d.Id, orderEvent.OrderId);
        var update = Builders<OrderReadDocument>.Update
            .Set(d => d.Status, orderEvent.ToStatus.ToString())
            .Set(d => d.UpdatedAt, orderEvent.CreatedAt);

        // Flow #7: OrderShippingAddressUpdated additionally projects the new address. The generic
        // Status/UpdatedAt Set above still runs harmlessly (for this event ToStatus == the unchanged
        // current status, since RecordEvent passes Status for both from/to), so we just layer the
        // extra .Set on. OrderStatusChangedByAdmin/OrderShipmentRequested need nothing beyond the
        // generic branch (the former advances Status, the latter is a same-value no-op).
        if (orderEvent.EventType == "OrderShippingAddressUpdated" && orderEvent.Payload is not null)
        {
            var payload = JsonSerializer.Deserialize<ShippingAddressUpdatedPayload>(orderEvent.Payload, PayloadOptions);
            if (payload?.Address is not null)
            {
                update = update.Set(d => d.ShippingAddress, new ShippingAddressReadDocument
                {
                    RecipientName = payload.Address.RecipientName,
                    Line1 = payload.Address.Line1,
                    Line2 = payload.Address.Line2,
                    City = payload.Address.City,
                    State = payload.Address.State,
                    PostalCode = payload.Address.PostalCode,
                    Country = payload.Address.Country,
                    Phone = payload.Address.Phone,
                });
            }
        }

        await readDbContext.Orders.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    private async Task ApplyOrderCreatedAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<OrderCreatedPayload>(orderEvent.Payload!, PayloadOptions)!;

        var filter = Builders<OrderReadDocument>.Filter.Eq(d => d.Id, orderEvent.OrderId);
        var update = Builders<OrderReadDocument>.Update
            .SetOnInsert(d => d.Id, orderEvent.OrderId)
            .SetOnInsert(d => d.UserId, payload.UserId)
            .SetOnInsert(d => d.Items, payload.Items.Select(i => new OrderLineItemReadDocument
            {
                Sku = i.Sku,
                Qty = i.Qty,
                UnitPrice = new MoneyReadDocument { Amount = i.UnitPrice.Amount, Currency = i.UnitPrice.Currency },
            }).ToList())
            .SetOnInsert(d => d.TotalAmount, new MoneyReadDocument { Amount = payload.Total, Currency = payload.Currency })
            .SetOnInsert(d => d.CreatedAt, orderEvent.CreatedAt)
            .Set(d => d.Status, orderEvent.ToStatus.ToString())
            .Set(d => d.UpdatedAt, orderEvent.CreatedAt);

        await readDbContext.Orders.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    private sealed record OrderCreatedPayload(
        Guid OrderId,
        Guid UserId,
        IReadOnlyList<OrderCreatedPayloadItem> Items,
        decimal Total,
        string Currency);

    private sealed record OrderCreatedPayloadItem(string Sku, int Qty, [property: JsonPropertyName("unitPrice")] OrderCreatedPayloadMoney UnitPrice);

    private sealed record OrderCreatedPayloadMoney(decimal Amount, string Currency);

    private sealed record ShippingAddressUpdatedPayload(Guid OrderId, ShippingAddressPayload? Address);

    private sealed record ShippingAddressPayload(
        string RecipientName,
        string Line1,
        string? Line2,
        string City,
        string State,
        string PostalCode,
        string Country,
        string? Phone);
}
