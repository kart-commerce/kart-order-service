using Kart.Shared.Domain;
using MediatR;

namespace KartOrderService.Application.Features.ConfirmOrderOnPaymentCompleted;

/// <summary>ORD-7 — consumes `PaymentCompleted` (`paymentIntentId`, `orderId`, `txnId`, `capturedAmount`, `currency`).</summary>
public sealed record ConsumePaymentCompletedCommand(Guid OrderId, Guid PaymentIntentId) : IRequest<Result>;
