using System.Text;
using System.Text.Json;
using Kart.Shared.Messaging;
using KartOrderService.Application.Features.AdvanceOnShipmentDispatched;
using KartOrderService.Application.Features.EnterFulfillmentException;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace KartOrderService.Infrastructure.Messaging;

/// <summary>ORD-9/ORD-11: consumes `order.shipping-events.queue` (bound to Shipping's `ShipmentDispatched`/`ShipmentCreationFailed`).</summary>
public sealed class ShippingEventsConsumerHostedService(
    IServiceScopeFactory scopeFactory,
    IConnectionFactory connectionFactory,
    MessageBusManifest manifest,
    ILogger<ShippingEventsConsumerHostedService> logger) : BackgroundService
{
    private const string QueueName = "order.shipping-events.queue";
    private const string RetryCountHeader = "x-order-shipping-events-retry-count";

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = connectionFactory.CreateConnection();
                using var channel = connection.CreateModel();
                RabbitMqTopologyProvisioner.Declare(channel, manifest);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += async (_, args) => await OnMessageReceivedAsync(channel, args, stoppingToken);
                channel.BasicConsume(QueueName, autoAck: false, consumer);

                while (!stoppingToken.IsCancellationRequested && connection.IsOpen)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Shipping-events consumer lost its RabbitMQ connection; reconnecting in {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task OnMessageReceivedAsync(IModel channel, BasicDeliverEventArgs args, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var json = Encoding.UTF8.GetString(args.Body.Span);

            var result = args.RoutingKey switch
            {
                "shipping.shipment.dispatched" => await sender.Send(ToDispatchedCommand(json), stoppingToken),
                "shipping.shipment.creation-failed" => await sender.Send(ToCreationFailedCommand(json), stoppingToken),
                _ => throw new InvalidOperationException($"Shipping-events consumer has no handling for routing key '{args.RoutingKey}'."),
            };

            if (result.IsFailure)
            {
                throw new InvalidOperationException($"Shipping-event handling failed: {result.Error.Code} - {result.Error.Message}");
            }

            channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            HandleFailure(channel, args, ex);
        }
    }

    private static ConsumeShipmentDispatchedCommand ToDispatchedCommand(string json)
    {
        var payload = Deserialize<ShipmentDispatchedPayload>(json);
        return new ConsumeShipmentDispatchedCommand(payload.OrderId, payload.TrackingId);
    }

    private static ConsumeShipmentCreationFailedCommand ToCreationFailedCommand(string json)
    {
        var payload = Deserialize<ShipmentCreationFailedPayload>(json);
        return new ConsumeShipmentCreationFailedCommand(payload.OrderId, payload.Reason);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? throw new InvalidOperationException($"{typeof(T).Name} payload deserialized to null.");

    private void HandleFailure(IModel channel, BasicDeliverEventArgs args, Exception ex)
    {
        var retryCount = RetryHeaders.GetRetryCount(args.BasicProperties, RetryCountHeader);
        var tiers = manifest.GetQueue(QueueName).RetryLadder?.Tiers ?? Array.Empty<RetryTierDefinition>();

        if (retryCount < tiers.Count)
        {
            var tier = tiers[retryCount];
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.Headers = new Dictionary<string, object> { [RetryCountHeader] = retryCount + 1 };

            channel.BasicPublish(exchange: string.Empty, routingKey: tier.Name, basicProperties: properties, body: args.Body);
            channel.BasicAck(args.DeliveryTag, multiple: false);

            logger.LogWarning(ex, "Handling shipping event ({RoutingKey}) failed; routed to retry tier {Tier} (attempt {Attempt}).", args.RoutingKey, tier.Name, retryCount + 1);
        }
        else
        {
            logger.LogCritical(ex, "Handling shipping event ({RoutingKey}) failed after exhausting all retry tiers; dead-lettering.", args.RoutingKey);
            channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private sealed record ShipmentDispatchedPayload(Guid OrderId, string Carrier, string TrackingId);
    private sealed record ShipmentCreationFailedPayload(Guid OrderId, string Reason);
}
