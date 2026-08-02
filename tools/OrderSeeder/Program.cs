using KartOrderService.Infrastructure.Persistence;
using KartOrderService.OrderSeeder;
using Microsoft.EntityFrameworkCore;

SeedOptions options;
try
{
    options = SeedOptions.Parse(args);
}
catch (ArgUsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var connectionString =
    options.ConnectionString
    ?? Environment.GetEnvironmentVariable("ORDER_DB_CONNECTION_STRING")
    ?? "Host=localhost;Port=5432;Database=kart_order;Username=postgres;Password=postgres";

Console.WriteLine($"Seeding {options.Count:N0} orders (batch size {options.BatchSize:N0}, " +
                   $"{(options.EmitEvents ? "emitting" : "not emitting")} outbox events)...");

var optionsBuilder = new DbContextOptionsBuilder<OrderDbContext>();
optionsBuilder.UseNpgsql(connectionString);

await using var db = new OrderDbContext(optionsBuilder.Options);

try
{
    await db.Database.CanConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not connect to the database: {ex.Message}");
    Console.Error.WriteLine("Is ORDER_DB_CONNECTION_STRING correct, and have migrations been applied (scripts/migrate.sh)?");
    return 1;
}

var seeder = new OrderBatchSeeder(db, options);
var result = await seeder.RunAsync();

Console.WriteLine();
Console.WriteLine($"Done: {result.OrdersCreated:N0} orders in {result.Elapsed.TotalSeconds:N1}s.");

return 0;
