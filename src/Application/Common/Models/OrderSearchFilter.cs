using KartOrderService.Domain.Orders;

namespace KartOrderService.Application.Common.Models;

/// <summary>
/// Flow #7 admin list/search criteria for <see cref="Interfaces.IOrderReadRepository.SearchAsync"/> —
/// every filter is optional; <see cref="Page"/>/<see cref="PageSize"/> are 1-based and validated at
/// the command layer. Results are sorted by <c>CreatedAt</c> descending.
/// </summary>
public sealed record OrderSearchFilter(
    OrderStatus? Status,
    Guid? UserId,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    int Page,
    int PageSize);
