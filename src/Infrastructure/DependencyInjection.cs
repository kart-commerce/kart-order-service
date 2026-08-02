using Kart.Shared.Messaging;
using KartOrderService.Application.Common.Compensation;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Infrastructure.Http;
using KartOrderService.Infrastructure.Messaging;
using KartOrderService.Infrastructure.Persistence;
using KartOrderService.Infrastructure.Persistence.ReadModel;
using KartOrderService.Infrastructure.ReconciliationSweep;
using KartOrderService.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Polly;
using Polly.CircuitBreaker;
using Polly.Extensions.Http;

namespace KartOrderService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        AddWriteSidePersistence(services, configuration);
        AddReadSidePersistence(services, configuration);
        AddMessaging(services, configuration);
        AddOutboundClients(services);

        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentPrincipal, HttpCurrentPrincipal>();
        services.AddScoped<InventoryReleaseCompensator>();
        services.AddMemoryCache();

        services.AddHostedService<ReconciliationSweepHostedService>();

        return services;
    }

    /// <summary>PostgreSQL — the sole write-side source of truth for the `Order` aggregate (database-design.md).</summary>
    private static void AddWriteSidePersistence(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<OrderDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("OrderDatabase"),
                // Order is always loaded with two owned collections (LineItems, Events) in the
                // same query - split-query avoids the cartesian-product row multiplication a
                // single query would otherwise produce for a two-collection load.
                npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
    }

    /// <summary>
    /// MongoDB — the CQRS read side `GET /v1/orders/{id}` (ORD-4) serves from, kept in sync by
    /// <see cref="OrderReadModelProjectorHostedService"/> polling `order_events` directly (see
    /// `contracts/README.md` — a deliberate deviation from `kart-payment-service`'s
    /// self-consumption pattern).
    /// </summary>
    private static void AddReadSidePersistence(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MongoOptions>(configuration.GetSection("Mongo"));

        services.AddSingleton<IMongoClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
            var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
            // requirement-spec.md's P95<150ms/P99<400ms read-path SLA: fail fast during a Mongo
            // outage rather than hang for the driver's 30s default server-selection timeout.
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            return new MongoClient(settings);
        });
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
            return new OrderReadDbContext(sp.GetRequiredService<IMongoClient>().GetDatabase(options.Database));
        });
        services.AddHostedService<MongoIndexInitializerHostedService>();

        services.AddScoped<IOrderReadRepository, OrderReadRepository>();
        services.AddScoped<ReadModelProjectionWriter>();
        services.AddHostedService<OrderReadModelProjectorHostedService>();
    }

    /// <summary>
    /// `contracts/message-bus-manifest.json` is the single source of truth for this service's
    /// entire RabbitMQ topology — nothing messaging-related is hardcoded in C#. One hosted service
    /// per consumer queue (ORD-6/7/8/9/10/11/13), plus the Outbox poller (ORD-2).
    /// </summary>
    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection("RabbitMq"));

        services.AddKartMessageBusManifest(sp => sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value.ManifestPath);
        services.AddKartRabbitMqConnectionFactory(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            return new RabbitMqConnectionSettings(options.HostName, options.Port, options.UserName, options.Password);
        });
        services.AddKartRabbitMqTopologyStartup();

        services.AddHostedService<OutboxRelayHostedService>();
        services.AddHostedService<InventoryEventsConsumerHostedService>();
        services.AddHostedService<PaymentEventsConsumerHostedService>();
        services.AddHostedService<ShippingEventsConsumerHostedService>();
        services.AddHostedService<TrackingEventsConsumerHostedService>();
    }

    /// <summary>
    /// architecture.md's synchronous outbound edges: Inventory reserve/release (2s timeout +
    /// circuit breaker, design-decisions.md), and Payment's compensation-refund call (looser
    /// timeout — a real gateway round-trip, not the tight write-path-gating case Inventory is).
    /// </summary>
    private static void AddOutboundClients(IServiceCollection services)
    {
        services.AddSingleton<ClientCredentialsTokenProvider>();

        services.AddHttpClient<IInventoryClient, InventoryClient>((sp, client) =>
            {
                var baseUrl = sp.GetRequiredService<IConfiguration>()["Inventory:BaseUrl"]
                    ?? throw new InvalidOperationException("Inventory:BaseUrl is not configured.");
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(2);
            })
            .AddPolicyHandler(CircuitBreakerPolicy());

        services.AddHttpClient<IPaymentClient, PaymentClient>((sp, client) =>
            {
                var baseUrl = sp.GetRequiredService<IConfiguration>()["Payment:BaseUrl"]
                    ?? throw new InvalidOperationException("Payment:BaseUrl is not configured.");
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(10);
            })
            .AddPolicyHandler(CircuitBreakerPolicy());
    }

    private static IAsyncPolicy<HttpResponseMessage> CircuitBreakerPolicy() =>
        HttpPolicyExtensions.HandleTransientHttpError()
            .Or<TimeoutException>()
            .CircuitBreakerAsync(handledEventsAllowedBeforeBreaking: 5, durationOfBreak: TimeSpan.FromSeconds(30));
}
