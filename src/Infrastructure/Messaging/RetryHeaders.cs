using System.Text;
using RabbitMQ.Client;

namespace KartOrderService.Infrastructure.Messaging;

/// <summary>Shared retry-count header parsing for this service's consumer hosted services (RabbitMQ has no built-in redelivery counter for the retry-ladder pattern).</summary>
public static class RetryHeaders
{
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
}
