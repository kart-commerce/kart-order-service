# Messaging Contract: kart-order-service (human-readable index over `message-bus-manifest.json`)

## Topology

- Own exchange: `order.exchange` (topic, durable). Own DLX: `order.dlx` (topic, durable).
- External exchanges bound against: `inventory.exchange`, `payment.exchange`, `shipping.exchange`, `tracking.exchange`.
- Four dedicated consumer queues, each with its own DLQ (never shared, per `event-standards.md`):
  `order.inventory-events.queue`, `order.payment-events.queue`, `order.shipping-events.queue`, `order.tracking-events.queue`.
- No `order.read-model-projection.queue` — deliberately, see `message-bus-manifest.json`'s own `_comment` and `contracts/README.md`.

## Published (5x exponential retry, `order.dlx` family, paged on-call — requirement-spec.md's elevated tier)

| Event | Routing Key | Consumers |
|---|---|---|
| `OrderCreated` | `order.order.created` | Payment, Notification, Review, Analytics |
| `OrderConfirmed` | `order.order.confirmed` | Shipping, Notification, Analytics |
| `OrderCancelled` | `order.order.cancelled` | Inventory, Offer, Notification, Analytics |
| `OrderCompensationTriggered` | `order.order.compensation-triggered` | Inventory, Notification, Analytics |
| `OrderDelivered` | `order.order.delivered` | Recommendation, Review, Notification, Analytics |

## Consumed

| Event | Publisher | Queue | Order's Reaction |
|---|---|---|---|
| `InventoryReserved` | Inventory | `order.inventory-events.queue` | Mark that line item's reservation confirmed; `Created→Reserved` once every line is confirmed (ORD-6) |
| `InventoryReservationFailed` | Inventory | `order.inventory-events.queue` | No-op/log — no order persists past the synchronous reserve call (ORD-6) |
| `PaymentCompleted` | Payment | `order.payment-events.queue` | `Reserved→Paid`, publish `OrderConfirmed` (ORD-7) |
| `PaymentFailed` | Payment | `order.payment-events.queue` | Release inventory, `OrderCompensationTriggered` → `Cancelled`, `OrderCancelled` (ORD-8) |
| `ChargebackReceived` | Payment | `order.payment-events.queue` | Conditional idempotent inventory release, direct `→Refunded` (ORD-13) — never calls Payment refund |
| `ShipmentDispatched` | Shipping | `order.shipping-events.queue` | `Paid→Shipped`, informational only (ORD-9) |
| `ShipmentCreationFailed` | Shipping | `order.shipping-events.queue` | `Paid→FulfillmentException` (ORD-11) |
| `DeliveryStatusUpdated` (terminal value only) | Delivery Tracking | `order.tracking-events.queue` | `Shipped→Delivered`, publish `OrderDelivered` (ORD-10); state-guarded NACK/requeue bounded to 60s if not yet `Shipped`, then `tracking.dlq` |

## Consumer Hosted Services (one per consumed event, `Kart.Shared.Messaging`'s `RabbitMqConsumerHostedServiceBase`)

`InventoryReservedConsumerHostedService`, `InventoryReservationFailedConsumerHostedService`,
`PaymentCompletedConsumerHostedService`, `PaymentFailedConsumerHostedService`,
`ChargebackReceivedConsumerHostedService`, `ShipmentDispatchedConsumerHostedService`,
`ShipmentCreationFailedConsumerHostedService`, `DeliveryStatusUpdatedConsumerHostedService` —
each deserializes its payload and dispatches to a MediatR command, then acks/nacks per the base
class's retry-count-header/DLQ mechanics.
