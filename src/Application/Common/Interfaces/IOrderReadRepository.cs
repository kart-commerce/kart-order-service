using KartOrderService.Application.Common.Models;

namespace KartOrderService.Application.Common.Interfaces;

/// <summary>ORD-4: `GetOrder` reads exclusively through this — the Mongo `order_read_model` collection, never PostgreSQL directly (BRD §7 CQRS).</summary>
public interface IOrderReadRepository
{
    Task<OrderViewDto?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken);
}
