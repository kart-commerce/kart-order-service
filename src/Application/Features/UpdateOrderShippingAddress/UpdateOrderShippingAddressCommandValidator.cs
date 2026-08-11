using FluentValidation;

namespace KartOrderService.Application.Features.UpdateOrderShippingAddress;

public sealed class UpdateOrderShippingAddressCommandValidator : AbstractValidator<UpdateOrderShippingAddressCommand>
{
    public UpdateOrderShippingAddressCommandValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.IdempotencyKey).NotEmpty();
        RuleFor(c => c.RecipientName).NotEmpty();
        RuleFor(c => c.Line1).NotEmpty();
        RuleFor(c => c.City).NotEmpty();
        RuleFor(c => c.State).NotEmpty();
        RuleFor(c => c.PostalCode).NotEmpty();
        RuleFor(c => c.Country).NotEmpty();
    }
}
