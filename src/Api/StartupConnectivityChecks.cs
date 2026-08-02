using KartOrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using RabbitMQ.Client;

namespace KartOrderService.Api;

/// <summary>
/// Verifies every infra dependency is reachable right after boot, one Connecting/connected log
/// pair per dependency, so a misconfigured or unreachable Postgres/Mongo/RabbitMQ shows up
/// immediately in the startup log instead of surfacing later as the first request's failure.
/// </summary>
public static class StartupConnectivityChecks
{
    public static async Task RunAsync(WebApplication app)
    {
        // WebApplicationFactory-based tests (Contract/Integration) mark themselves "Testing" and
        // swap in Testcontainers-backed dependencies of their own choosing - this step is a
        // deliberate no-op there, matching kart-identity-service's own precedent.
        if (app.Environment.IsEnvironment("Testing"))
        {
            return;
        }

        var logger = app.Logger;

        await CheckAsync(logger, "PostgresDB", async () =>
        {
            using var scope = app.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await dbContext.Database.CanConnectAsync();
        });

        await CheckAsync(logger, "MongoDB", async () =>
        {
            var mongoClient = app.Services.GetRequiredService<IMongoClient>();
            await mongoClient.ListDatabaseNamesAsync();
        });

        await CheckAsync(logger, "RabbitMQ", () =>
        {
            var connectionFactory = app.Services.GetRequiredService<IConnectionFactory>();
            using var connection = connectionFactory.CreateConnection();
            return Task.CompletedTask;
        });
    }

    private static async Task CheckAsync(ILogger logger, string dependency, Func<Task> connect)
    {
        logger.LogInformation("Connecting Order {Dependency} ...", dependency);
        try
        {
            await connect();
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Failed to connect to Order {Dependency}", dependency);
            throw;
        }

        logger.LogInformation("{Dependency} connected", dependency);
    }
}
