using System.Text;
using System.Text.Json;
using Kart.Shared.Messaging;
using KartOrderService.Application.Features.CompensateOnPaymentFailed;
using KartOrderService.Application.Features.ConfirmOrderOnPaymentCompleted;
using KartOrderService.Application.Features.ReactToChargeback;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace KartOrderService.Infrastructure.Messaging;

/// <summary>ORD-7/ORD-8/ORD-13: consumes `order.payment-events.queue` (bound to Payment's `PaymentCompleted`/`PaymentFailed`/`ChargebackReceived`).</summary>
public sealed class PaymentEventsConsumerHostedService(
    IServiceScopeFactory scopeFactory,
    IConnectionFactory connectionFactory,
    MessageBusManifest manifest,
    ILogger<PaymentEventsConsumerHostedService> logger) : BackgroundService
{
    private const string QueueName = "order.payment-events.queue";
    private const string RetryCountHeader = "x-order-payment-events-retry-count";

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
                logger.LogError(ex, "Payment-events consumer lost its RabbitMQ connection; reconnecting in {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task OnMessageReceivedAsync(IModel channel, BasicDeliverEventArgs args, CancellationToken stoppingToken)
    {
        // Continue the W3C trace carried on the inbound message's headers (a trace that started in
        // Payment and flows into Order via this queue), so this consumer's work links to the
        // original TraceId in Tempo/Loki instead of starting a disconnected root trace.
        using var activity = RabbitMqTraceContext.StartConsumeActivity(QueueName, args.BasicProperties);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var json = Encoding.UTF8.GetString(args.Body.Span);

            var result = args.RoutingKey switch
            {
                "payment.intent.completed" => await sender.Send(ToCompletedCommand(json), stoppingToken),
                "payment.intent.failed" => await sender.Send(ToFailedCommand(json), stoppingToken),
                "payment.chargeback.received" => await sender.Send(ToChargebackCommand(json), stoppingToken),
                _ => throw new InvalidOperationException($"Payment-events consumer has no handling for routing key '{args.RoutingKey}'."),
            };

            if (result.IsFailure)
            {
                throw new InvalidOperationException($"Payment-event handling failed: {result.Error.Code} - {result.Error.Message}");
            }

            channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            HandleFailure(channel, args, ex);
        }
    }

    private static ConsumePaymentCompletedCommand ToCompletedCommand(string json)
    {
        var payload = Deserialize<PaymentCompletedPayload>(json);
        return new ConsumePaymentCompletedCommand(payload.OrderId, payload.PaymentIntentId);
    }

    private static ConsumePaymentFailedCommand ToFailedCommand(string json)
    {
        var payload = Deserialize<PaymentFailedPayload>(json);
        return new ConsumePaymentFailedCommand(payload.OrderId, payload.Reason);
    }

    private static ConsumeChargebackReceivedCommand ToChargebackCommand(string json)
    {
        var payload = Deserialize<ChargebackReceivedPayload>(json);
        return new ConsumeChargebackReceivedCommand(payload.OrderId, payload.ChargebackId);
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

            logger.LogWarning(ex, "Handling payment event ({RoutingKey}) failed; routed to retry tier {Tier} (attempt {Attempt}).", args.RoutingKey, tier.Name, retryCount + 1);
        }
        else
        {
            logger.LogCritical(ex, "Handling payment event ({RoutingKey}) failed after exhausting all retry tiers; dead-lettering — paged on-call per the elevated tier (event-contract.md).", args.RoutingKey);
            channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private sealed record PaymentCompletedPayload(Guid PaymentIntentId, Guid OrderId, string TxnId, decimal CapturedAmount, string Currency);
    private sealed record PaymentFailedPayload(Guid PaymentIntentId, Guid OrderId, string Reason, decimal CapturedAmount, string Currency);
    private sealed record ChargebackReceivedPayload(Guid PaymentIntentId, Guid OrderId, string ChargebackId, decimal Amount, string Currency, string Reason);
}
