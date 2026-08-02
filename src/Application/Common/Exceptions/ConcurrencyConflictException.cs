namespace KartOrderService.Application.Common.Exceptions;

/// <summary>
/// Translated from EF Core's `DbUpdateConcurrencyException` by `EfUnitOfWork` — the runtime
/// manifestation of database-design.md's compare-and-swap concurrency mechanism (`Status` is
/// configured as an EF concurrency token; zero rows affected on save throws this). Consumer/sweep
/// callers catch this directly and apply their own retry/no-op/nack policy; HTTP-facing callers
/// (`CancelOrder`, `ResolveFulfillmentException`) let it propagate — mapped to `409` via
/// `Kart.Shared.ErrorHandling` in `Program.cs`.
/// </summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);
