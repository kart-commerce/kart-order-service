using FluentValidation;

namespace KartOrderService.Application.Features.CreateOrder;

public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(c => c.IdempotencyKey).NotEmpty();
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.Currency).NotEmpty();
        RuleFor(c => c.Items).NotEmpty();
        RuleForEach(c => c.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Sku).NotEmpty();
            item.RuleFor(i => i.Qty).GreaterThanOrEqualTo(1);
            item.RuleFor(i => i.UnitPrice).GreaterThanOrEqualTo(0);
        });
    }
}
