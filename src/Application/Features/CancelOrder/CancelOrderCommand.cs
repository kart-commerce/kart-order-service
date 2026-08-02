using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using MediatR;

namespace KartOrderService.Application.Features.CancelOrder;

/// <summary>ORD-5 — `api-contract.yaml`'s `POST /v1/orders/{id}/cancel`. Naturally idempotent via `Order.TryCancel`'s own state-guard (no-op if already `Cancelled`) — no separate idempotency ledger needed (unlike `CreateOrder`), so `IdempotencyKey` here is accepted/required at the API boundary but not itself compared against a stored payload.</summary>
public sealed record CancelOrderCommand(Guid OrderId, string IdempotencyKey) : IRequest<Result<OrderViewDto>>;
