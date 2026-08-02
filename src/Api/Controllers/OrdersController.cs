using KartOrderService.Api.Common;
using KartOrderService.Api.Security;
using KartOrderService.Application.Common.Models;
using KartOrderService.Application.Features.CancelOrder;
using KartOrderService.Application.Features.CreateOrder;
using KartOrderService.Application.Features.GetOrder;
using KartOrderService.Application.Features.ResolveFulfillmentException;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KartOrderService.Api.Controllers;

[ApiController]
[Route("v1/orders")]
[Authorize]
public sealed class OrdersController(ISender sender) : ControllerBase
{
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

    /// <summary>ORD-4: api-contract.yaml `GET /v1/orders/{id}` — served from the MongoDB read model.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderViewDto>> Get([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetOrderQuery(id), cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Ok(dto));
    }

    /// <summary>ORD-5: api-contract.yaml `POST /v1/orders/{id}/cancel` — legal only pre-`Shipped`.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OrderViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderViewDto>> Cancel(
        [FromRoute] Guid id,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CancelOrderCommand(id, idempotencyKey), cancellationToken);
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
        var result = await sender.Send(new ResolveFulfillmentExceptionCommand(id, request.Action, idempotencyKey), cancellationToken);
        return this.ToActionResult<OrderViewDto, OrderViewDto>(result, dto => Ok(dto));
    }
}
