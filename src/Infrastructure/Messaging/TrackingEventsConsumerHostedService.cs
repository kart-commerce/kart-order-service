using System.Text;
using System.Text.Json;
using Kart.Shared.Messaging;
using KartOrderService.Application.Features.CompleteOrderOnDelivery;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace KartOrderService.Infrastructure.Messaging;

/// <summary>
/// ORD-10: consumes `order.tracking-events.queue` (bound to Delivery Tracking's
/// `DeliveryStatusUpdated`). Order "consumes only its terminal 'delivered' status value"
/// (requirement-spec.md) — every non-terminal update is acked as a plain no-op here, never
/// dispatched to the handler at all. A terminal update against an order not yet `Shipped` (or with
/// no order recorded for this `trackingId` yet) fails the handler, which this consumer's normal
/// failure path nacks into the retry ladder — the bounded "hold, then DLQ" behavior
/// design-decisions.md specifies (see `ConsumeDeliveryStatusUpdatedCommandHandler`'s own remarks).
/// </summary>
public sealed class TrackingEventsConsumerHostedService(
    IServiceScopeFactory scopeFactory,
    IConnectionFactory connectionFactory,
    MessageBusManifest manifest,
    ILogger<TrackingEventsConsumerHostedService> logger) : BackgroundService
{
    private const string QueueName = "order.tracking-events.queue";
    private const string RetryCountHeader = "x-order-tracking-events-retry-count";
    private const string TerminalStatus = "Delivered";

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
                logger.LogError(ex, "Tracking-events consumer lost its RabbitMQ connection; reconnecting in {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task OnMessageReceivedAsync(IModel channel, BasicDeliverEventArgs args, CancellationToken stoppingToken)
    {
        try
        {
            var json = Encoding.UTF8.GetString(args.Body.Span);
            var payload = Deserialize<DeliveryStatusUpdatedPayload>(json);

            if (!string.Equals(payload.Status, TerminalStatus, StringComparison.OrdinalIgnoreCase))
            {
                channel.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var result = await sender.Send(new ConsumeDeliveryStatusUpdatedCommand(payload.TrackingId), stoppingToken);

            if (result.IsFailure)
            {
                throw new InvalidOperationException($"DeliveryStatusUpdated handling failed: {result.Error.Code} - {result.Error.Message}");
            }

            channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            HandleFailure(channel, args, ex);
        }
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

            logger.LogWarning(ex, "Handling terminal DeliveryStatusUpdated failed (order not yet Shipped, or not found); routed to retry tier {Tier} (attempt {Attempt}).", tier.Name, retryCount + 1);
        }
        else
        {
            logger.LogCritical(ex, "Handling terminal DeliveryStatusUpdated failed after exhausting all retry tiers; dead-lettering per design-decisions.md's 60s ordering-guard window.");
            channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private sealed record DeliveryStatusUpdatedPayload(string TrackingId, string Status);
}
