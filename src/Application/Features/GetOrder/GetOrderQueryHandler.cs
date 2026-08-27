using Kart.Shared.Domain;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Models;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.GetOrder;

/// <summary>ORD-4: served exclusively from the Mongo read model (BRD §7 CQRS), never PostgreSQL directly.</summary>
public sealed class GetOrderQueryHandler(IOrderReadRepository readRepository, ILogger<GetOrderQueryHandler> logger) : IRequestHandler<GetOrderQuery, Result<OrderViewDto>>
{
    public async Task<Result<OrderViewDto>> Handle(GetOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await readRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Stage {Stage}: order {OrderId} was not found in the read model", "GetOrderNotFound", request.OrderId);
            return Result.Failure<OrderViewDto>(Error.NotFound($"Order {request.OrderId} was not found."));
        }

        logger.LogInformation("Stage {Stage}: order {OrderId} detail served from the read model", "OrderDetailProcessCompleted", request.OrderId);
        return Result.Success(order);
    }
}
