namespace KartOrderService.Infrastructure.Auditing;

/// <summary>
/// Order is the first service on the platform to register a concrete `IAuditLogWriter`
/// (`Kart.Shared.Auditing`'s own README notes no consumer had one yet) — money/saga-adjacent
/// mutations (`CancelOrder`, `ResolveFulfillmentException`) warrant a real audit trail beyond the
/// inline `created_by`/`updated_by` columns already stamped on every write. A best-effort,
/// independent write (its own `SaveChangesAsync`, not folded into the calling handler's own
/// transaction) — losing an audit row to a rare post-commit failure is an acceptable trade-off
/// against the added complexity of atomically coupling it to every possible caller's transaction.
/// </summary>
public sealed class OrderAuditLogEntry
{
    public Guid Id { get; private set; }

    public string ServiceName { get; private set; } = string.Empty;

    public string ActorId { get; private set; } = string.Empty;

    public string ActorType { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public string? MetadataJson { get; private set; }

    private OrderAuditLogEntry()
    {
    }

    public static OrderAuditLogEntry Create(string serviceName, string actorId, string actorType, string action, string entityType, string entityId, DateTimeOffset occurredAt, string? metadataJson) =>
        new()
        {
            Id = Guid.NewGuid(),
            ServiceName = serviceName,
            ActorId = actorId,
            ActorType = actorType,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OccurredAt = occurredAt,
            MetadataJson = metadataJson,
        };
}
