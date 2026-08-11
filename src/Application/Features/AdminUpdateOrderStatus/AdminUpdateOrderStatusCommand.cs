using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using KartOrderService.Domain.Orders;
using MediatR;

namespace KartOrderService.Application.Features.AdminUpdateOrderStatus;

/// <summary>Flow #7 — `api-contract.yaml`'s `PATCH /v1/orders/{id}/status`. Admin ops-recovery fallback to manually advance a stalled order.</summary>
public sealed record AdminUpdateOrderStatusCommand(
    Guid OrderId,
    OrderStatus TargetStatus,
    string Reason,
    string IdempotencyKey) : IRequest<Result<OrderViewDto>>;
