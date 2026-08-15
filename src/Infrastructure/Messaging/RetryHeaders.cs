using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace KartOrderService.Infrastructure.Messaging;

/// <summary>Shared retry-count header parsing for this service's consumer hosted services (RabbitMQ has no built-in redelivery counter for the retry-ladder pattern).</summary>
public static class RetryHeaders
{
    /// <summary>
    /// A TTL-ladder retry bounces a message through the default exchange with the routing key set
    /// to the retry-tier queue's own name (each consumer's own `HandleFailure` does
    /// `BasicPublish(exchange: "", routingKey: tier.Name, ...)`) - so by the time RabbitMQ
    /// redelivers it to the main queue via that tier's `x-dead-letter-routing-key`,
    /// <see cref="BasicDeliverEventArgs.RoutingKey"/> no longer reflects the routing key the
    /// message originally arrived with (it reads as the retry-tier queue's own name instead).
    /// Every consumer that branches on the routing key to decide which event type/command a
    /// message maps to must read this header via <see cref="GetEffectiveRoutingKey"/> instead of
    /// <c>RoutingKey</c> directly, or a retried message throws "no handling for routing key" on
    /// its very first redelivery and never actually recovers - confirmed live 2026-08-12 on
    /// `order.payment-events.queue` during Inventory & Stock Management flow testing (see
    /// kart-product-service's own `RetryLadderDispatcher.cs`, the precedent this mirrors).
    /// </summary>
    private const string OriginalRoutingKeyHeader = "x-order-original-routing-key";

    public static int GetRetryCount(IBasicProperties properties, string headerName)
    {
        if (properties.Headers is not null && properties.Headers.TryGetValue(headerName, out var value))
        {
            return value switch
            {
                int i => i,
                long l => (int)l,
                byte[] bytes => int.Parse(Encoding.UTF8.GetString(bytes)),
                _ => 0,
            };
        }

        return 0;
    }

    /// <summary>The routing key this message actually arrived with on its very first delivery, regardless of how many retry-ladder bounces it has since been through.</summary>
    public static string GetEffectiveRoutingKey(BasicDeliverEventArgs delivery)
    {
        if (delivery.BasicProperties.Headers is not null
            && delivery.BasicProperties.Headers.TryGetValue(OriginalRoutingKeyHeader, out var value)
            && value is byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return delivery.RoutingKey;
    }

    /// <summary>Stamps the message's true original routing key onto the outgoing retry-tier publish's headers, so <see cref="GetEffectiveRoutingKey"/> can recover it after any number of bounces.</summary>
    public static void StampOriginalRoutingKey(IDictionary<string, object> headers, BasicDeliverEventArgs delivery) =>
        headers[OriginalRoutingKeyHeader] = GetEffectiveRoutingKey(delivery);
}
