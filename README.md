# kart-order-service

The platform's sole Saga orchestrator — owns the order lifecycle state machine
(`Created → Reserved → Paid → Shipped → Delivered → Cancelled/Refunded`, extended with the
non-terminal `FulfillmentException` hold state) and coordinates Inventory, Payment, Shipping, and
Delivery Tracking across a distributed, compensable transaction. See `contracts/` for the full
approved design (requirement-spec, architecture, ddd-model, database-design, design-decisions,
edge-cases, api-contract, event-contract) and `contracts/README.md` for the two small,
explicitly-flagged implementation addendums this build needed.

## Layout

Clean Architecture + Vertical Slice, per `docs/standards/folder-structure.md` (`agent-reusables`):

```
src/
├── Api/             ASP.NET Core host — controllers, security, health checks, Program.cs
├── Application/     MediatR vertical slices (Application/Features/<UseCase>), Common/{Interfaces,Models,Behaviors,Exceptions}
├── Domain/          Order aggregate, OrderLineItem, OrderEvent, OrderStatus, Money — zero framework deps
└── Infrastructure/  EF Core (Postgres write side), MongoDB (read side), RabbitMQ (manifest-driven), HTTP clients, reconciliation sweep
```

`Domain` never references `Infrastructure`/`Api`. `Application` depends only on interfaces
`Infrastructure` implements (dependency inversion) — no direct EF Core/Mongo/RabbitMQ types leak
above `Infrastructure`.

CQRS: PostgreSQL is the write-side source of truth (`orders`/`order_items`/`order_events` —
`order_events` doubles as the transactional Outbox). MongoDB is the read side `GET /v1/orders/{id}`
serves from, kept in sync by `OrderReadModelProjectorHostedService` polling `order_events`
directly (not a RabbitMQ self-consumption loop — see `contracts/README.md`).

## Running locally

```bash
docker compose up -d          # Postgres, Mongo, RabbitMQ, and the service itself
./scripts/migrate.sh          # apply EF Core migrations (or run inside the Dockerfile.migrate image)
./scripts/seed-orders.sh 100  # optional: seed 100 fake orders directly against Postgres
```

Copy `src/Api/appsettings.Local.json.example` → `src/Api/appsettings.Local.json` (gitignored) and
point `GlobalConfig:Path` at your own external secrets file (`kart-conventions.md` Configuration
Management — see `Kart.Shared.Configuration`'s README for the full bootstrap mechanism).

## Migrations

EF Core, via `scripts/migrate.sh` (wraps `dotnet ef database update` / `dotnet ef migrations add`)
or the standalone `Dockerfile.migrate` image (bundles `dotnet-ef` into a self-contained executable
reading `ORDER_DB_CONNECTION_STRING`, no .NET runtime needed at deploy time).

## Seed data

`tools/OrderSeeder/` — a standalone console project generating fake orders directly against
Postgres, mirroring `kart-category-service/tools/CategorySeeder`'s CLI ergonomics
(`--count`, `--batch-size`, `--seed`, `--connection`, `--principal`, `--emit-events`).

## Tests

`tests/UnitTests` (domain transition-graph + handler tests), `tests/IntegrationTests`
(`WebApplicationFactory` + Testcontainers Postgres/Mongo/RabbitMQ), `tests/ContractTests`
(validates live responses against `contracts/api-contract.yaml`).
