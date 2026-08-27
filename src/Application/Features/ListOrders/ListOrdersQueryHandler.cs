using Kart.Shared.Domain;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Models;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.ListOrders;

/// <summary>Flow #7: read-only (no transaction) — reads exclusively from the Mongo read model via <see cref="IOrderReadRepository"/>.</summary>
public sealed class ListOrdersQueryHandler(
    IOrderReadRepository readRepository,
    ILogger<ListOrdersQueryHandler> logger) : IRequestHandler<ListOrdersQuery, Result<PagedOrdersDto>>
{
    public async Task<Result<PagedOrdersDto>> Handle(ListOrdersQuery request, CancellationToken cancellationToken)
    {
        var filter = new OrderSearchFilter(request.Status, request.UserId, request.CreatedFrom, request.CreatedTo, request.Page, request.PageSize);
        var (items, totalCount) = await readRepository.SearchAsync(filter, cancellationToken);

        logger.LogInformation("Stage {Stage}: order list served, {ReturnedCount} of {TotalCount} matching order(s)", "OrderListProcessCompleted", items.Count, totalCount);
        return Result.Success(new PagedOrdersDto(items, totalCount, request.Page, request.PageSize));
    }
}
