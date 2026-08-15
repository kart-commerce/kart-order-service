using Kart.Shared.Domain;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Models;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Features.GenerateInvoice;

/// <summary>
/// Flow #7: builds an invoice view for an order that has a completed payment, read-only from the
/// Mongo read model (NOT a new transaction). Only legal for orders whose status is one of
/// {Paid, Shipped, Delivered, FulfillmentException, Refunded} — a Created/Reserved/Cancelled order
/// has no completed payment to invoice, so it returns a <c>Conflict</c>.
///
/// <para>The invoice number is deterministic from the order id (no new storage), and Subtotal ==
/// Total because order-service has no separate tax or shipping-fee line.</para>
/// </summary>
public sealed class GenerateInvoiceQueryHandler(
    IOrderReadRepository readRepository,
    ILogger<GenerateInvoiceQueryHandler> logger) : IRequestHandler<GenerateInvoiceQuery, Result<InvoiceDto>>
{
    private static readonly HashSet<string> InvoiceableStatuses = ["Paid", "Shipped", "Delivered", "FulfillmentException", "Refunded"];

    public async Task<Result<InvoiceDto>> Handle(GenerateInvoiceQuery request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Stage {Stage}: invoice requested for order {OrderId}", "GenerateInvoiceHandlerStarted", request.OrderId);

        var order = await readRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Stage {Stage}: invoice rejected, order {OrderId} was not found", "GenerateInvoiceNotFound", request.OrderId);
            return Result.Failure<InvoiceDto>(Error.NotFound($"Order {request.OrderId} was not found."));
        }

        if (!InvoiceableStatuses.Contains(order.Status))
        {
            logger.LogWarning("Stage {Stage}: order {OrderId} has status '{Status}' — not yet invoiceable", "GenerateInvoiceNotEligible", request.OrderId, order.Status);
            return Result.Failure<InvoiceDto>(Error.Conflict($"Order {request.OrderId} has status '{order.Status}' — an invoice is only available once payment has completed (Paid or later)."));
        }

        logger.LogInformation("Stage {Stage}: order {OrderId} is invoiceable (status '{Status}')", "GenerateInvoiceEligibleBranch", order.OrderId, order.Status);

        var invoiceNumber = "INV-" + request.OrderId.ToString("N")[..10].ToUpperInvariant();

        var invoice = new InvoiceDto(
            invoiceNumber,
            order.OrderId,
            order.UserId,
            order.Status,
            order.Items,
            order.TotalAmount,
            order.TotalAmount,
            order.ShippingAddress,
            order.CreatedAt,
            DateTimeOffset.UtcNow);

        logger.LogInformation("Stage {Stage}: invoice {InvoiceNumber} generated for order {OrderId}", "GenerateInvoiceProcessCompleted", invoiceNumber, order.OrderId);

        return Result.Success(invoice);
    }
}
