using KartOrderService.Application.Common.Models;

namespace KartOrderService.Application.Common.Interfaces;

/// <summary>ORD-4: `GetOrder` reads exclusively through this — the Mongo `order_read_model` collection, never PostgreSQL directly (BRD §7 CQRS).</summary>
public interface IOrderReadRepository
{
    Task<OrderViewDto?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Flow #7 admin list/search — filtered, paged, sorted by `CreatedAt` descending. Returns the page plus the total matching count.</summary>
    Task<(IReadOnlyList<OrderSummaryDto> Items, long TotalCount)> SearchAsync(OrderSearchFilter filter, CancellationToken cancellationToken);
}
