using KartOrderService.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KartOrderService.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="OrderAuditLogEntry"/> to `order_audit_log` — a plain, independent table, not part of database-design.md's schema (this service is the first to register a concrete `IAuditLogWriter`; see that class's own remarks).</summary>
public sealed class OrderAuditLogEntryConfiguration : IEntityTypeConfiguration<OrderAuditLogEntry>
{
    public void Configure(EntityTypeBuilder<OrderAuditLogEntry> builder)
    {
        builder.ToTable("order_audit_log");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.ServiceName).HasColumnName("service_name").IsRequired();
        builder.Property(e => e.ActorId).HasColumnName("actor_id").IsRequired();
        builder.Property(e => e.ActorType).HasColumnName("actor_type").IsRequired();
        builder.Property(e => e.Action).HasColumnName("action").IsRequired();
        builder.Property(e => e.EntityType).HasColumnName("entity_type").IsRequired();
        builder.Property(e => e.EntityId).HasColumnName("entity_id").IsRequired();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(e => e.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb");

        builder.HasIndex(e => new { e.EntityType, e.EntityId }).HasDatabaseName("idx_order_audit_log_entity");
    }
}
