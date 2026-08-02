using Bogus;
using KartOrderService.Domain.Orders;
using KartOrderService.Infrastructure.Persistence;

namespace KartOrderService.OrderSeeder;

public sealed record SeedResult(int OrdersCreated, TimeSpan Elapsed);

/// <summary>
/// Generates fake orders directly against Postgres, driving each one through
/// <see cref="Order"/>'s own guarded transition methods rather than hand-writing rows — seeded
/// data is therefore always a domain-valid state, exercising the identical invariants a real Saga
/// run would. Batched for large-volume seeding, mirroring `kart-category-service/tools/CategorySeeder`'s
/// CLI ergonomics.
/// </summary>
public sealed class OrderBatchSeeder(OrderDbContext dbContext, SeedOptions options)
{
    private static readonly string[] Skus = ["SKU-WIDGET-001", "SKU-GADGET-002", "SKU-GIZMO-003", "SKU-DOOHICKEY-004", "SKU-THINGAMAJIG-005"];
    private static readonly string[] Currencies = ["USD", "EUR", "GBP"];

    public async Task<SeedResult> RunAsync()
    {
        var faker = options.RandomSeed.HasValue ? new Faker { Random = new Randomizer(options.RandomSeed.Value) } : new Faker();
        var started = DateTimeOffset.UtcNow;
        var created = 0;
        var now = DateTimeOffset.UtcNow;

        while (created < options.Count)
        {
            var batchCount = Math.Min(options.BatchSize, options.Count - created);
            for (var i = 0; i < batchCount; i++)
            {
                var order = BuildRandomOrder(faker, now);
                MarkAlreadyRelayed(order, now);
                dbContext.Orders.Add(order);
            }

            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            created += batchCount;
            Console.WriteLine($"  ... {created:N0}/{options.Count:N0}");
        }

        return new SeedResult(created, DateTimeOffset.UtcNow - started);
    }

    private Order BuildRandomOrder(Faker faker, DateTimeOffset now)
    {
        var currency = faker.PickRandom(Currencies);
        var itemCount = faker.Random.Int(1, 3);
        var items = Enumerable.Range(0, itemCount)
            .Select(_ => new CreateOrderLineItem(
                faker.PickRandom(Skus),
                faker.Random.Int(1, 5),
                faker.Random.Decimal(5, 250),
                currency,
                Guid.NewGuid())) // fake reservationId — no real Inventory call backs this seeded row.
            .ToList();

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(), items, options.ActingPrincipal, now);

        // A minority advance further through the lifecycle for realistic read-model/query variety —
        // driven through the aggregate's own guarded methods, never a hand-set Status column.
        var roll = faker.Random.Double();
        if (roll < 0.5)
        {
            return order;
        }

        foreach (var item in order.LineItems)
        {
            order.MarkLineItemReservationConfirmed(item.Sku, options.ActingPrincipal, now);
        }

        order.TryAdvanceToReserved(options.ActingPrincipal, now);
        if (roll < 0.65)
        {
            return order;
        }

        order.TryAdvanceToPaid(Guid.NewGuid(), options.ActingPrincipal, now);
        if (roll < 0.8)
        {
            return order;
        }

        order.TryAdvanceToShipped($"TRACK-{faker.Random.AlphaNumeric(10).ToUpperInvariant()}", options.ActingPrincipal, now);
        if (roll < 0.95)
        {
            return order;
        }

        order.TryAdvanceToDelivered(options.ActingPrincipal, now);
        return order;
    }

    /// <summary>
    /// Default (`--emit-events` off): stamps every seeded row's outbox marker as already-published,
    /// so a seed run doesn't spam RabbitMQ/other services unless explicitly asked to.
    /// `ProjectedAt` is deliberately left unset regardless — the local read-model projector is a
    /// purely internal Postgres→Mongo sync with no cross-service fan-out, so seeded orders should
    /// always become queryable via `GET /v1/orders/{id}` once it next runs.
    /// </summary>
    private void MarkAlreadyRelayed(Order order, DateTimeOffset now)
    {
        if (options.EmitEvents)
        {
            return;
        }

        foreach (var orderEvent in order.Events.Where(e => e.EventType is not null))
        {
            orderEvent.MarkPublished(now);
        }
    }
}
