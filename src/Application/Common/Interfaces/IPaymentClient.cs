namespace KartOrderService.Application.Common.Interfaces;

public enum PaymentRefundOutcome
{
    Accepted,
    Conflict,
    Unavailable,
}

public sealed record PaymentRefundResult(PaymentRefundOutcome Outcome);

/// <summary>
/// architecture.md's Compensation-Refund Trigger — Order's synchronous `POST /payments/{id}/refund`
/// call, used only by ORD-12's `cancel` action (`FulfillmentException→Cancelled`). Never called for
/// `ChargebackReceived` (ORD-13) — the bank has already reversed the charge externally.
/// </summary>
public interface IPaymentClient
{
    /// <summary><paramref name="idempotencyKey"/> is deterministically derived from `(orderId, paymentIntentId, "compensation-refund")`, matching `kart-payment-service/architecture.md`'s documented mechanism.</summary>
    Task<PaymentRefundResult> RefundAsync(Guid paymentIntentId, decimal amount, string currency, string idempotencyKey, CancellationToken cancellationToken);
}
