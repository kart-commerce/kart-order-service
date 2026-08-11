using Kart.Shared.Observability;
using KartOrderService.Api.Common;
using KartOrderService.Api.Security;
using KartOrderService.Application.Common.Models;
using KartOrderService.Application.Features.AdminUpdateOrderStatus;
using KartOrderService.Application.Features.CancelOrder;
using KartOrderService.Application.Features.CreateOrder;
using KartOrderService.Application.Features.GenerateInvoice;
using KartOrderService.Application.Features.GetOrder;
using KartOrderService.Application.Features.ListOrders;
using KartOrderService.Application.Features.RequestShipment;
using KartOrderService.Application.Features.ResolveFulfillmentException;
using KartOrderService.Application.Features.UpdateOrderShippingAddress;
using KartOrderService.Domain.Orders;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KartOrderService.Api.Controllers;

[ApiController]
[Route("v1/orders")]
[Authorize]
public sealed class OrdersController(ISender sender, ILogger<OrdersController> logger) : ControllerBase
{
    private const string FlowName = "OrderManagementAdmin";

    /// <summary>ORD-1: api-contract.yaml `POST /v1/orders` — requires `Idempotency-Key`. Synchronously reserves Inventory per line item before returning `202`.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<OrderViewDto>> Create(
        [FromBody] CreateOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var items = request.Items
            .Select(i => new CreateOrderLineItemRequest(i.Sku, i.Qty, i.UnitPrice.Amount))
            .ToList();

        var result = await sender.Send(new CreateOrderCommand(idempotencyKey, request.UserId, items, request.Currency), cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Accepted(dto));
    }

    /// <summary>Flow #7: api-contract.yaml `GET /v1/orders` — the admin Order Management list/search view. Admin-only (no customer self-service list exists).</summary>
    [HttpGet]
    [Authorize(Policy = AuthenticationExtensions.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(PagedOrdersDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedOrdersDto>> List(
        [FromQuery] OrderStatus? status,
        [FromQuery] Guid? userId,
        [FromQuery] DateTimeOffset? createdFrom,
        [FromQuery] DateTimeOffset? createdTo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        using var _ = KartFlowContext.Push(FlowName);
        logger.LogInformation("Stage {Stage}: order list requested (status={Status}, userId={UserId}, page={Page})", "OrderListRequested", status, userId, page);

        var result = await sender.Send(new ListOrdersQuery(status, userId, createdFrom, createdTo, page, pageSize), cancellationToken);
        return this.ToActionResult<PagedOrdersDto, PagedOrdersDto>(result, dto => Ok(dto));
    }

    /// <summary>ORD-4: api-contract.yaml `GET /v1/orders/{id}` — served from the MongoDB read model.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderViewDto>> Get([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);
        logger.LogInformation("Stage {Stage}: order detail requested for {OrderId}", "OrderDetailRequested", id);

        var result = await sender.Send(new GetOrderQuery(id), cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Ok(dto));
    }

    /// <summary>ORD-5: api-contract.yaml `POST /v1/orders/{id}/cancel` — legal only pre-`Shipped`. Optional body carries a cancellation reason.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderViewDto>> Cancel(
        [FromRoute] Guid id,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken,
        [FromBody] CancelOrderRequest? request = null)
    {
        using var _ = KartFlowContext.Push(FlowName);
        logger.LogInformation("Stage {Stage}: cancel requested for order {OrderId}", "OrderCancelRequested", id);

        var result = await sender.Send(new CancelOrderCommand(id, idempotencyKey, request?.Reason), cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Ok(dto));
    }

    /// <summary>Flow #7: api-contract.yaml `PATCH /v1/orders/{id}/shipping-address` — admin attach/correct of the delivery address, legal only pre-`Shipped`.</summary>
    [HttpPatch("{id:guid}/shipping-address")]
    [Authorize(Policy = AuthenticationExtensions.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderViewDto>> UpdateShippingAddress(
        [FromRoute] Guid id,
        [FromBody] UpdateShippingAddressRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);
        logger.LogInformation("Stage {Stage}: shipping-address update requested for order {OrderId}", "OrderShippingAddressUpdateRequested", id);

        var command = new UpdateOrderShippingAddressCommand(
            id, request.RecipientName, request.Line1, request.Line2, request.City,
            request.State, request.PostalCode, request.Country, request.Phone, idempotencyKey);

        var result = await sender.Send(command, cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Ok(dto));
    }

    /// <summary>Flow #7: api-contract.yaml `PATCH /v1/orders/{id}/status` — admin ops-recovery manual status advance ({Shipped, Delivered, FulfillmentException}).</summary>
    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = AuthenticationExtensions.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderViewDto>> UpdateStatus(
        [FromRoute] Guid id,
        [FromBody] AdminUpdateOrderStatusRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);
        logger.LogInformation("Stage {Stage}: status update requested for order {OrderId} -> {TargetStatus}", "OrderStatusUpdateRequested", id, request.TargetStatus);

        if (!Enum.TryParse<OrderStatus>(request.TargetStatus, ignoreCase: true, out var targetStatus))
        {
            return this.MapFailure(Kart.Shared.Domain.Error.Custom("validation_error", $"'{request.TargetStatus}' is not a recognized order status."));
        }

        var result = await sender.Send(new AdminUpdateOrderStatusCommand(id, targetStatus, request.Reason, idempotencyKey), cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Ok(dto));
    }

    /// <summary>Flow #7: api-contract.yaml `GET /v1/orders/{id}/invoice` — admin invoice view, derived from the read model.</summary>
    [HttpGet("{id:guid}/invoice")]
    [Authorize(Policy = AuthenticationExtensions.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InvoiceDto>> GetInvoice([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);
        logger.LogInformation("Stage {Stage}: invoice requested for order {OrderId}", "OrderInvoiceRequested", id);

        var result = await sender.Send(new GenerateInvoiceQuery(id), cancellationToken);
        return this.ToActionResult<InvoiceDto, InvoiceDto>(result, dto => Ok(dto));
    }

    /// <summary>Flow #7: api-contract.yaml `POST /v1/orders/{id}/request-shipment` — records the admin's intent to ship a paid order.</summary>
    [HttpPost("{id:guid}/request-shipment")]
    [Authorize(Policy = AuthenticationExtensions.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderViewDto>> RequestShipment(
        [FromRoute] Guid id,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);
        logger.LogInformation("Stage {Stage}: shipment request received for order {OrderId}", "OrderShipmentRequestReceived", id);

        var result = await sender.Send(new RequestShipmentCommand(id, idempotencyKey), cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Ok(dto));
    }

    /// <summary>ORD-12: api-contract.yaml `POST /v1/orders/{id}/resolve-fulfillment-exception` — internal, Admin Service's client-credentials principal only.</summary>
    [HttpPost("{id:guid}/resolve-fulfillment-exception")]
    [Authorize(Policy = AuthenticationExtensions.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderViewDto>> ResolveFulfillmentException(
        [FromRoute] Guid id,
        [FromBody] ResolveFulfillmentExceptionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var _ = KartFlowContext.Push(FlowName);
        logger.LogInformation("Stage {Stage}: fulfillment-exception resolve requested for order {OrderId} (action={Action})", "OrderFulfillmentExceptionResolveRequested", id, request.Action);

        var result = await sender.Send(new ResolveFulfillmentExceptionCommand(id, request.Action, idempotencyKey), cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Ok(dto));
    }
}
