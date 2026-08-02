using KartOrderService.Domain.Orders;
using KartOrderService.Infrastructure.Auditing;
using KartOrderService.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace KartOrderService.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL write side (database-design.md) — the sole source of truth for the `Order`
/// aggregate. `OrderLineItem`/`OrderEvent` are owned child collections (same aggregate/transaction
/// boundary), always loaded and saved with their parent `Order` — there is no separate `DbSet` for
/// either. There is deliberately no MongoDB anywhere in this DbContext; the read side is a
/// separate, eventually-consistent projection kept in sync by
/// <see cref="Messaging.OrderReadModelProjectorHostedService"/> polling `order_events` directly
/// (see `contracts/README.md`). `AuditLogEntries` backs <see cref="EfAuditLogWriter"/> — a plain,
/// independent table, not part of the `Order` aggregate itself.
/// </summary>
public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderAuditLogEntry> AuditLogEntries => Set<OrderAuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OrderConfiguration());
        modelBuilder.ApplyConfiguration(new OrderAuditLogEntryConfiguration());
    }
}
