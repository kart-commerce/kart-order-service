# contracts/

`api-contract.yaml` and `event-contract.md` are synced, unmodified copies of the approved design
package published at `kart-shared/contracts/kart-order-service/` (itself byte-identical to
`kart-platform/docs/services/kart-order-service/`). They are the single source of truth this
service builds and tests against. Update them only by re-copying the upstream files, never by
hand-editing here.

`message-bus-manifest.json` did not exist upstream (order-service was never scaffolded before this
build) - it is authored here directly from `event-contract.md`'s Published/Consumed tables plus
the actual routing keys already declared in `kart-inventory-service`, `kart-payment-service`,
`kart-shipping-service`, and `kart-delivery-tracking-service`'s own manifests, following
`kart-identity-service`/`kart-payment-service`'s manifest-as-JSON-source-of-truth convention.

## Deviations / Implementation Addendums (intentional, flagged per platform convention)

Following the same "flag it, don't silently invent it" convention `kart-payment-service/contracts/
api-contract.yaml`'s header and `kart-inventory-service/contracts/api-contract.yaml`'s
"Implementation Addendum" section already use for their own gaps:

1. **`order_items` gains two nullable columns: `reservation_id`, `reservation_confirmed_at`.**
   `database-design.md`'s `order_items` schema has neither. They're required because
   `kart-inventory-service`'s real, approved `POST /inventory/reserve` contract reserves exactly
   one `(sku, qty)` pair per call - not a whole order - so `CreateOrder` fans out one reserve call
   per line item and must track each line's own `reservationId` to later release it (pre-
   confirmation compensation, `FulfillmentException` cancellation, chargeback reaction). Without
   this, ORD-8/ORD-12/ORD-13's release step would have no reservation id to release.
   `reservation_confirmed_at` lets ORD-6's `InventoryReserved` consumer track per-line confirmation
   so `Created→Reserved` only fires once every line item's reservation is confirmed.

2. **`order_events` gains one nullable column: `projected_at`.** `database-design.md`'s own Read
   Model section states the Mongo projector "reads directly off the Outbox stream via its own
   internal subscription... it also needs the internal `to_status` transitions" - i.e. every
   transition row, not just the subset with `event_type IS NOT NULL` the outbox poller's
   `published_at`/`idx_outbox_unpublished` already track. The projector needs its own, independent
   progress marker so it doesn't only see business-event rows.

3. **Cross-service gap, not fixed here (flagged for the next design pass on Payment's side, per
   `kart-payment-service/contracts/README.md`'s own deviation #4):** Payment's contract assumes the
   consumed `OrderCreated` event carries a `gatewayToken` field ("without which the async
   Order→Payment charge trigger cannot function"). Order's own approved `event-contract.md`/
   `api-contract.yaml` do not include one - `POST /v1/orders`'s request body is `userId`/`items`/
   `currency` only, and `OrderCreated`'s key fields are `orderId`/`userId`/`items`/`total`. This
   build does not invent a `gatewayToken` field on Order's side (that would be scope Order's own
   approved contract never asked for, and how a client-side payment-method token would even reach
   `POST /orders` is a checkout-UI/Payment-side concern, not an Order one). Left as an explicit,
   documented cross-service reconciliation item.

4. **`order_events.from_status` is nullable, not `NOT NULL` as `database-design.md`'s CREATE TABLE
   literally states.** The same document's own prose for `POST /orders`'s initial insert requires
   `from_status = NULL` for the first row (no prior status to compare against) — a `NOT NULL`
   constraint would reject the exact insert its own transition-mechanics section describes. Treated
   as a documentation inconsistency to correct, not a design decision to escalate.

5. **`orders`/`order_events` are NOT physically range-partitioned by month, despite
   `database-design.md`'s explicit instruction.** Real PostgreSQL requires any UNIQUE index on a
   partitioned table to include every partition-key column. Partitioning `orders` by `created_at`
   would therefore force `idx_orders_idempotency_key` to become `UNIQUE (idempotency_key,
   created_at)` instead of `UNIQUE (idempotency_key)` alone — and two genuinely concurrent
   duplicate `POST /orders` requests generate two distinct `created_at` timestamps (each request's
   own `DateTimeOffset.UtcNow`), so the composite constraint would let **both** inserts succeed,
   silently defeating the platform's single most emphasized invariant for this service ("no double
   orders," ddd-model.md's Idempotent creation invariant). Reusing Payment's separate
   `IdempotencyRecord` ledger table to sidestep this was considered and rejected — ddd-model.md
   explicitly contrasts Order against that exact pattern and states the key belongs directly on
   `Order` for a documented reason (no external call between reserve and confirm). Given the
   choice between the explicitly-mandated correctness invariant and an operational/archival scale
   optimization (this build's Postgres instance is nowhere near the BRD's 20M-orders/day flash-sale
   figure the partitioning was sized for), correctness wins: both tables are created as ordinary,
   unpartitioned tables here, with every other column/index/RLS policy exactly as specified.
   Reintroducing monthly partitioning correctly (composite keys cascading into `order_items`'/
   `order_events`' foreign keys, or a properly-designed uniqueness mechanism independent of the
   partition key) is a follow-up operational task, not a day-one requirement this build silently
   papers over.

Everything else in this directory matches the platform-approved design package exactly.
