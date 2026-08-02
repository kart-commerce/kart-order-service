namespace KartOrderService.Infrastructure.Persistence.ReadModel;

/// <summary>Binds the `"Mongo"` config section.</summary>
public sealed class MongoOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;
}
