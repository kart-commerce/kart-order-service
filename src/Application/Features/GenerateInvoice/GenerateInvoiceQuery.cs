using Kart.Shared.Domain;
using KartOrderService.Application.Common.Models;
using MediatR;

namespace KartOrderService.Application.Features.GenerateInvoice;

/// <summary>Flow #7 — `api-contract.yaml`'s `GET /v1/orders/{id}/invoice`. Admin-only; read-only, derived entirely from the read model.</summary>
public sealed record GenerateInvoiceQuery(Guid OrderId) : IRequest<Result<InvoiceDto>>;
