namespace KartOrderService.Domain;

/// <summary>
/// ddd-model.md's audit-actor invariant (BRD §24.3): every Saga-step transition driven by an event
/// consumer or the reconciliation sweep — i.e. with no human/API caller — stamps one of these
/// well-known ids as `created_by`/`updated_by`, never a client-supplied value and never `NULL`.
/// Client-facing writes (`POST /orders`, `.../cancel`) stamp the owning `userId` instead;
/// `resolve-fulfillment-exception` stamps Admin Service's client-credentials principal.
/// </summary>
public static class SystemPrincipals
{
    public const string InventoryConsumer = "system:order-saga-inventory-consumer";
    public const string PaymentConsumer = "system:order-saga-payment-consumer";
    public const string ShippingConsumer = "system:order-saga-shipping-consumer";
    public const string DeliveryConsumer = "system:order-saga-delivery-consumer";
    public const string ChargebackConsumer = "system:order-saga-chargeback-consumer";
    public const string ReconciliationSweep = "system:order-reconciliation-sweep";
    public const string OutboxPoller = "system:order-outbox-poller";
    public const string ReadModelProjector = "system:order-read-model-projector";
}
