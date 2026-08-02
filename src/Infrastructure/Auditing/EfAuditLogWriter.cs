using System.Text.Json;
using Kart.Shared.Auditing;
using KartOrderService.Infrastructure.Persistence;

namespace KartOrderService.Infrastructure.Auditing;

/// <summary>The concrete `IAuditLogWriter` this service registers instead of the shared package's `NullAuditLogWriter` default.</summary>
public sealed class EfAuditLogWriter(OrderDbContext dbContext) : IAuditLogWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        var metadataJson = entry.Metadata is null ? null : JsonSerializer.Serialize(entry.Metadata, SerializerOptions);

        dbContext.AuditLogEntries.Add(OrderAuditLogEntry.Create(
            entry.ServiceName, entry.ActorId, entry.ActorType, entry.Action, entry.EntityType, entry.EntityId, entry.OccurredAt, metadataJson));

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
