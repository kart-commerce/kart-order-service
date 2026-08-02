using Kart.Shared.Domain;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Models;
using MediatR;

namespace KartOrderService.Application.Features.GetOrder;

/// <summary>ORD-4: served exclusively from the Mongo read model (BRD §7 CQRS), never PostgreSQL directly.</summary>
public sealed class GetOrderQueryHandler(IOrderReadRepository readRepository) : IRequestHandler<GetOrderQuery, Result<OrderViewDto>>
{
    public async Task<Result<OrderViewDto>> Handle(GetOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await readRepository.GetByIdAsync(request.OrderId, cancellationToken);
        return order is null
            ? Result.Failure<OrderViewDto>(Error.NotFound($"Order {request.OrderId} was not found."))
            : Result.Success(order);
    }
}
