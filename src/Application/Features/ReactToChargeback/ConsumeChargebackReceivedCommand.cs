using Kart.Shared.Domain;
using MediatR;

namespace KartOrderService.Application.Features.ReactToChargeback;

/// <summary>ORD-13 — consumes `ChargebackReceived` (`orderId`, `paymentIntentId`, `chargebackId`, `amount`, `reason`, ADR-0012).</summary>
public sealed record ConsumeChargebackReceivedCommand(Guid OrderId, string ChargebackId) : IRequest<Result>;
