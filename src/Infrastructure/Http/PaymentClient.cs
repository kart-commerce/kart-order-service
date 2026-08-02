using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KartOrderService.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace KartOrderService.Infrastructure.Http;

/// <summary>
/// architecture.md's Compensation-Refund Trigger — ORD-12's `cancel` action calls
/// `POST /payments/{id}/refund` directly (not gateway-proxied), authenticated as the
/// `orderServicePrincipal` client-credentials principal
/// (`kart-payment-service/contracts/api-contract.yaml`). Never called for `ChargebackReceived`
/// (ORD-13) — the bank has already reversed the charge externally.
/// </summary>
public sealed class PaymentClient(HttpClient httpClient, ClientCredentialsTokenProvider tokenProvider, ILogger<PaymentClient> logger) : IPaymentClient
{
    public async Task<PaymentRefundResult> RefundAsync(Guid paymentIntentId, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{paymentIntentId}/refund")
            {
                Content = JsonContent.Create(new { amount = new { amount, currency } }),
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);

            var accessToken = await tokenProvider.GetAccessTokenAsync(cancellationToken);
            if (accessToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return new PaymentRefundResult(PaymentRefundOutcome.Accepted);
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return new PaymentRefundResult(PaymentRefundOutcome.Conflict);
            }

            logger.LogWarning("Payment refund for intent {PaymentIntentId} returned unexpected status {StatusCode}.", paymentIntentId, response.StatusCode);
            return new PaymentRefundResult(PaymentRefundOutcome.Unavailable);
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException or HttpRequestException or BrokenCircuitException)
        {
            logger.LogWarning(ex, "Payment refund for intent {PaymentIntentId} timed out or its circuit breaker is open.", paymentIntentId);
            return new PaymentRefundResult(PaymentRefundOutcome.Unavailable);
        }
    }
}
