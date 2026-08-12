using FluentValidation;
using KartOrderService.Domain.Orders;

namespace KartOrderService.Application.Features.AdminUpdateOrderStatus;

/// <summary>
/// The actual scope boundary of Flow #7's manual status advance (NOT the domain, which stays
/// generic): an admin may only drive an order toward {Shipped, Delivered, FulfillmentException}.
/// Payment/Reserved/Cancelled/Refunded are deliberately unreachable from this endpoint — those are
/// event-driven or money-adjacent transitions with their own dedicated flows.
/// </summary>
public sealed class AdminUpdateOrderStatusCommandValidator : AbstractValidator<AdminUpdateOrderStatusCommand>
{
    private static readonly OrderStatus[] AllowedTargets = [OrderStatus.Shipped, OrderStatus.Delivered, OrderStatus.FulfillmentException];

    public AdminUpdateOrderStatusCommandValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.IdempotencyKey).NotEmpty();
        RuleFor(c => c.Reason).NotEmpty();
        RuleFor(c => c.TargetStatus)
            .Must(s => AllowedTargets.Contains(s))
            .WithMessage("targetStatus must be one of 'Shipped', 'Delivered', or 'FulfillmentException'.");
    }
}
