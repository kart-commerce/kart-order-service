using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace KartOrderService.IntegrationTests;

/// <summary>
/// Real Postgres + Mongo + RabbitMQ via Testcontainers - end-to-end coverage of the actual
/// unique-constraint/RLS/concurrency-token guarantees (EF Core InMemory doesn't enforce real DB
/// constraints, so the double-order/idempotency-race protections can only be genuinely proven
/// against a real Postgres). `IInventoryClient`/`IPaymentClient` are replaced with in-process fakes
/// — no real kart-inventory-service/kart-payment-service is available to these tests, mirroring
/// `kart-payment-service`'s own `SimulatedPaymentGatewayAdapter` precedent.
/// </summary>
public sealed class OrderApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("kart_order_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly MongoDbContainer _mongo = new MongoDbBuilder()
        .WithImage("mongo:7.0")
        .Build();

    // RabbitMQ's default "guest" user is restricted to loopback-only connections - a container
    // port mapped out to the host does not count as loopback, so a dedicated non-guest user is
    // required for the test process to authenticate at all.
    private const string RabbitMqUser = "test";
    private const string RabbitMqPassword = "test";

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder()
        .WithImage("rabbitmq:3-management-alpine")
        .WithUsername(RabbitMqUser)
        .WithPassword(RabbitMqPassword)
        .Build();

    public FakeInventoryClient InventoryClient { get; } = new();

    public FakePaymentClient PaymentClient { get; } = new();

    // Kart.Shared.Configuration.AddKartGlobalConfig requires GlobalConfig:Path to point at a real,
    // readable JSON file (kart-conventions.md's Configuration Management bootstrap) - tests have no
    // real per-machine secrets file, so this is an empty placeholder purely to satisfy that check.
    private readonly string _globalConfigPath = Path.Combine(Path.GetTempPath(), $"kart-order-service-test-globalconfig-{Guid.NewGuid():N}.json");

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(_globalConfigPath, "{}");

        // `AddKartGlobalConfig()` reads `builder.Configuration` directly in Program.cs, BEFORE
        // `builder.Build()` runs - WebApplicationFactory's ConfigureWebHost overrides (below) are
        // only visible to code resolving IConfiguration from the built DI container, not to this
        // pre-Build read. An environment variable is part of WebApplicationBuilder's own default
        // provider set from the moment CreateBuilder(args) runs, so it's the one override
        // mechanism that reaches this specific pre-Build call.
        Environment.SetEnvironmentVariable("GlobalConfig__Path", _globalConfigPath);

        await Task.WhenAll(_postgres.StartAsync(), _mongo.StartAsync(), _rabbitMq.StartAsync());

        // Migrate via a standalone DbContext, NOT one resolved from `Services` - the first access
        // to `Services` builds and starts the whole host, including every registered
        // IHostedService (Outbox poller, read-model projector, Saga consumers, reconciliation
        // sweep), which would otherwise start querying `orders`/`order_events` before this
        // migration has created them.
        var optionsBuilder = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(_postgres.GetConnectionString());
        await using (var migrationContext = new OrderDbContext(optionsBuilder.Options))
        {
            await migrationContext.Database.MigrateAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderDatabase"] = _postgres.GetConnectionString(),
                ["Mongo:ConnectionString"] = _mongo.GetConnectionString(),
                ["Mongo:Database"] = "kart_order_read_test",
                ["RabbitMq:HostName"] = _rabbitMq.Hostname,
                ["RabbitMq:Port"] = _rabbitMq.GetMappedPublicPort(5672).ToString(),
                ["RabbitMq:UserName"] = RabbitMqUser,
                ["RabbitMq:Password"] = RabbitMqPassword,
                ["Inventory:BaseUrl"] = "http://unused.invalid",
                ["Payment:BaseUrl"] = "http://unused.invalid",
                ["GlobalConfig:Path"] = _globalConfigPath,
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IInventoryClient>();
            services.AddSingleton<IInventoryClient>(InventoryClient);

            services.RemoveAll<IPaymentClient>();
            services.AddSingleton<IPaymentClient>(PaymentClient);
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        File.Delete(_globalConfigPath);
        await _postgres.DisposeAsync();
        await _mongo.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await base.DisposeAsync();
    }
}
