using KartOrderService.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KartOrderService.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Order"/> (and its owned <see cref="OrderLineItem"/>/<see cref="OrderEvent"/>
/// child collections — same aggregate/transaction boundary, ddd-model.md) to
/// `orders`/`order_items`/`order_events`, verbatim from database-design.md plus the three
/// addendum columns documented in `contracts/README.md`. `orders`/`order_events`' monthly range
/// partitioning and row-level-security policies are hand-authored raw SQL in the initial migration
/// (EF Core's model has no native concept of either) — see `Migrations/*_InitialCreate.cs`.
/// </summary>
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(o => o.OrderId);
        builder.Property(o => o.OrderId).HasColumnName("order_id").ValueGeneratedNever();

        builder.Property(o => o.UserId).HasColumnName("user_id").IsRequired();

        // database-design.md's compare-and-swap concurrency mechanism (Order.cs's own XML doc):
        // `IsConcurrencyToken()` makes EF Core's own SaveChangesAsync perform the literal
        // `UPDATE orders SET status=$new WHERE order_id=$id AND status=$expected` the doc
        // specifies, throwing DbUpdateConcurrencyException on zero rows affected.
        builder.Property(o => o.Status)
            .HasColumnName("status")
            .HasConversion(status => status.ToString(), value => Enum.Parse<OrderStatus>(value))
            .HasMaxLength(24)
            .IsRequired()
            .IsConcurrencyToken();

        builder.Property(o => o.TotalAmount).HasColumnName("total_amount").HasColumnType("numeric(12,2)").IsRequired();
        builder.Property(o => o.Currency).HasColumnName("currency").IsRequired();

        builder.Property(o => o.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.HasIndex(o => o.IdempotencyKey).IsUnique().HasDatabaseName("idx_orders_idempotency_key");
        builder.HasIndex(o => o.UserId).HasDatabaseName("idx_orders_user_id");
        builder.HasIndex(o => new { o.Status, o.CreatedAt }).HasDatabaseName("idx_orders_status_created");

        // Addendum #3 (contracts/README.md) — not in database-design.md's orders schema.
        builder.Property(o => o.PaymentIntentId).HasColumnName("payment_intent_id");

        // Addendum #6 (contracts/README.md) — not in database-design.md's orders schema.
        builder.Property(o => o.TrackingId).HasColumnName("tracking_id");
        builder.HasIndex(o => o.TrackingId).HasDatabaseName("idx_orders_tracking_id");

        builder.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(o => o.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(o => o.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(o => o.UpdatedBy).HasColumnName("updated_by").IsRequired();

        builder.OwnsMany(o => o.LineItems, item =>
        {
            item.ToTable("order_items");
            item.WithOwner().HasForeignKey(i => i.OrderId);
            item.HasKey(i => i.Id);
            item.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
            item.Property(i => i.OrderId).HasColumnName("order_id");
            item.Property(i => i.Sku).HasColumnName("sku").IsRequired();
            item.Property(i => i.Qty).HasColumnName("qty").IsRequired();
            item.Property(i => i.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(12,2)").IsRequired();
            item.Property(i => i.Currency).HasColumnName("currency").IsRequired();

            // Addendum #1 (contracts/README.md) — not in database-design.md's order_items schema.
            item.Property(i => i.ReservationId).HasColumnName("reservation_id");
            item.Property(i => i.ReservationConfirmedAt).HasColumnName("reservation_confirmed_at");

            item.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
            item.Property(i => i.UpdatedAt).HasColumnName("updated_at").IsRequired();
            item.Property(i => i.CreatedBy).HasColumnName("created_by").IsRequired();
            item.Property(i => i.UpdatedBy).HasColumnName("updated_by").IsRequired();

            item.HasIndex(i => i.OrderId).HasDatabaseName("idx_order_items_order_id");
        });
        builder.Navigation(o => o.LineItems).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(o => o.Events, evt =>
        {
            evt.ToTable("order_events");
            evt.WithOwner().HasForeignKey(e => e.OrderId);
            evt.HasKey(e => e.Id);
            evt.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            evt.Property(e => e.OrderId).HasColumnName("order_id");
            evt.Property(e => e.Sequence).HasColumnName("sequence").IsRequired();

            // Nullable — see contracts/README.md addendum #4 (database-design.md's CREATE TABLE
            // literally says NOT NULL, but its own prose requires NULL for POST /orders's first row).
            evt.Property(e => e.FromStatus)
                .HasColumnName("from_status")
                .HasConversion(
                    status => status.HasValue ? status.Value.ToString() : null,
                    value => value == null ? null : Enum.Parse<OrderStatus>(value))
                .HasMaxLength(24);

            evt.Property(e => e.ToStatus)
                .HasColumnName("to_status")
                .HasConversion(status => status.ToString(), value => Enum.Parse<OrderStatus>(value))
                .HasMaxLength(24)
                .IsRequired();

            evt.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(64);
            evt.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");

            evt.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            evt.Property(e => e.PublishedAt).HasColumnName("published_at");
            evt.Property(e => e.ProjectedAt).HasColumnName("projected_at"); // Addendum #2 (contracts/README.md).
            evt.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            evt.Property(e => e.CreatedBy).HasColumnName("created_by").IsRequired();
            evt.Property(e => e.UpdatedBy).HasColumnName("updated_by").IsRequired();

            evt.HasIndex(e => new { e.OrderId, e.Sequence }).IsUnique().HasDatabaseName("idx_order_events_order_seq");
            evt.HasIndex(e => e.CreatedAt).HasDatabaseName("idx_outbox_unpublished")
                .HasFilter("event_type IS NOT NULL AND published_at IS NULL");
        });
        builder.Navigation(o => o.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
