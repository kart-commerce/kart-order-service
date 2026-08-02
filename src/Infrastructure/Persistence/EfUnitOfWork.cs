using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace KartOrderService.Infrastructure.Persistence;

public sealed class EfUnitOfWork(OrderDbContext dbContext) : IUnitOfWork
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private IDbContextTransaction? _transaction;

    /// <summary>
    /// database-design.md's RLS section: opens the transaction and immediately issues the two
    /// session-scoped principal settings, before any query runs. `set_config(..., true)` — not a
    /// literal `SET LOCAL` string — because Postgres's `SET`/`SET LOCAL` statements do not accept
    /// bind parameters at the wire-protocol level; `set_config` is the parameterized equivalent
    /// (the `true` third argument is what makes it transaction-scoped, matching `SET LOCAL`).
    /// </summary>
    public async Task BeginPrincipalScopedTransactionAsync(string actingPrincipal, string principalKind, CancellationToken cancellationToken)
    {
        _transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.current_principal', {actingPrincipal}, true)", cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.current_principal_kind', {principalKind}, true)", cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("A concurrent writer already moved this order to a different status; the compare-and-swap was lost.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            throw new DuplicateKeyException("A unique constraint was violated (idx_orders_idempotency_key) — a concurrent request already created this order.");
        }
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.CommitAsync(cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.RollbackAsync(cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
    }
}
