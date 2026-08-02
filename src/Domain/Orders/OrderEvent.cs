namespace KartOrderService.Domain.Orders;

/// <summary>
/// ddd-model.md's `OrderEvent` child entity — one append-only row per state transition, doubling
/// as both the audit trail and the Outbox relay row (design-decisions.md "Outbox Table Strategy":
/// no second, unlisted `outbox` table exists). `EventType`/`Payload` are set only for the subset of
/// transitions that also carry a published business event; `FromStatus` is nullable — the initial
/// `POST /orders` insert has no prior status to compare against, contradicting database-design.md's
/// literal `from_status VARCHAR(24) NOT NULL` DDL, which its own prose immediately violates for
/// exactly that row (see `contracts/README.md` addendum #4 for this documented correction).
/// </summary>
public sealed class OrderEvent
{
    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>Monotonic per `OrderId` (edge-cases.md "Outbox Publish Failure/Reordering After DB Commit") — the reordering-detection guard for Saga-critical consumers.</summary>
    public int Sequence { get; private set; }

    public OrderStatus? FromStatus { get; private set; }

    public OrderStatus ToStatus { get; private set; }

    /// <summary>Null for internal-only transitions with no published counterpart (e.g. `Created→Reserved`, `Paid→Shipped`).</summary>
    public string? EventType { get; private set; }

    /// <summary>JSON outbox payload; present only when <see cref="EventType"/> is set.</summary>
    public string? Payload { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Set by <see cref="Infrastructure.Messaging.OutboxRelayHostedService"/> (ORD-2) once actually published to RabbitMQ.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Addendum #2 (`contracts/README.md`): set by the Mongo read-model projector (ORD-3) once this row — published or not — has been applied to the read model.</summary>
    public DateTimeOffset? ProjectedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public string UpdatedBy { get; private set; } = "system:order-outbox-poller";

    /// <summary>EF Core materialization only.</summary>
    private OrderEvent()
    {
    }

    internal OrderEvent(Guid id, Guid orderId, int sequence, OrderStatus? fromStatus, OrderStatus toStatus, string? eventType, string? payload, string actingPrincipal, DateTimeOffset now)
    {
        Id = id;
        OrderId = orderId;
        Sequence = sequence;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        EventType = eventType;
        Payload = payload;
        CreatedAt = now;
        UpdatedAt = now;
        CreatedBy = actingPrincipal;
    }

    public void MarkPublished(DateTimeOffset publishedAt)
    {
        PublishedAt = publishedAt;
        UpdatedAt = publishedAt;
        UpdatedBy = "system:order-outbox-poller";
    }

    public void MarkProjected(DateTimeOffset projectedAt)
    {
        ProjectedAt = projectedAt;
    }
}
