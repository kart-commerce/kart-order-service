namespace KartOrderService.Infrastructure.Messaging;

/// <summary>Binds the `"RabbitMq"` config section. `ManifestPath` resolves against `AppContext.BaseDirectory` at runtime — the manifest ships next to the compiled DLL (`KartOrderService.Api.csproj`'s `<Content Include>` link).</summary>
public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string ManifestPath { get; set; } = "message-bus-manifest.json";
}
