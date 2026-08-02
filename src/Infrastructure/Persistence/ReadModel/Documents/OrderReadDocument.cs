using MongoDB.Bson.Serialization.Attributes;

namespace KartOrderService.Infrastructure.Persistence.ReadModel.Documents;

/// <summary>database-design.md's Read Model section, verbatim shape — the `order_read_model` collection `GET /v1/orders/{id}` (ORD-4) serves from, never PostgreSQL directly.</summary>
public sealed class OrderReadDocument
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid UserId { get; set; } = Guid.Empty;

    public string Status { get; set; } = string.Empty;

    public List<OrderLineItemReadDocument> Items { get; set; } = [];

    public MoneyReadDocument TotalAmount { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OrderLineItemReadDocument
{
    public string Sku { get; set; } = string.Empty;

    public int Qty { get; set; }

    public MoneyReadDocument UnitPrice { get; set; } = new();
}

public sealed class MoneyReadDocument
{
    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;
}
