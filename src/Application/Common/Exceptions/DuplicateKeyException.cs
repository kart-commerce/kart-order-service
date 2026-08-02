namespace KartOrderService.Application.Common.Exceptions;

/// <summary>
/// Translated from a Postgres unique-violation (`idx_orders_idempotency_key`) by `EfUnitOfWork` —
/// the database-enforced backstop behind `CreateOrderCommandHandler`'s own existence-check race
/// (two concurrent `POST /orders` calls with the same `Idempotency-Key`). Caught directly by
/// `CreateOrderCommandHandler` to reload and replay the winner's order, never surfaced to a client
/// as a raw 500.
/// </summary>
public sealed class DuplicateKeyException(string message) : Exception(message);
