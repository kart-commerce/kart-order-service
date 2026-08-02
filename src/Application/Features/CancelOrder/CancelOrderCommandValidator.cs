using FluentValidation;

namespace KartOrderService.Application.Features.CancelOrder;

public sealed class CancelOrderCommandValidator : AbstractValidator<CancelOrderCommand>
{
    public CancelOrderCommandValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.IdempotencyKey).NotEmpty();
    }
}
