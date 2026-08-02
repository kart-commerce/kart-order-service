using Kart.Shared.Domain;
using MediatR;

namespace KartOrderService.Application.Features.CompensateOnPaymentFailed;

/// <summary>ORD-8 — consumes `PaymentFailed` (`paymentIntentId`, `orderId`, `reason`, `capturedAmount`, `currency`).</summary>
public sealed record ConsumePaymentFailedCommand(Guid OrderId, string Reason) : IRequest<Result>;
