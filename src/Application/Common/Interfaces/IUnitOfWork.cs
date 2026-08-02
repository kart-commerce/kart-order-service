namespace KartOrderService.Application.Common.Interfaces;

/// <summary>
/// The write-side Unit of Work — `OrderDbContext` is the implementation (EF Core's `DbContext`
/// already is the Unit of Work, per ddd-cqrs-standards.md; no separate abstraction beyond this
/// thin interface). `BeginPrincipalScopedTransactionAsync` opens the transaction and immediately
/// issues `SET LOCAL app.current_principal[_kind]` before any query runs (database-design.md's RLS
/// section) — every handler/consumer/sweep that mutates an `Order` must use this instead of a bare
/// `BeginTransactionAsync`.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Throws <see cref="Exceptions.ConcurrencyConflictException"/> on a lost compare-and-swap, or <see cref="Exceptions.DuplicateKeyException"/> on a unique-constraint violation.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task BeginPrincipalScopedTransactionAsync(string actingPrincipal, string principalKind, CancellationToken cancellationToken);

    Task CommitTransactionAsync(CancellationToken cancellationToken);

    Task RollbackTransactionAsync(CancellationToken cancellationToken);
}
