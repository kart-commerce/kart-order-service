using KartOrderService.Application.Common.Interfaces;

namespace KartOrderService.IntegrationTests;

/// <summary>Test double for the compensation-refund call — no real kart-payment-service is available to these integration tests.</summary>
public sealed class FakePaymentClient : IPaymentClient
{
    public Task<PaymentRefundResult> RefundAsync(Guid paymentIntentId, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(new PaymentRefundResult(PaymentRefundOutcome.Accepted));
}
