using FluentValidation;

namespace KartOrderService.Application.Features.ResolveFulfillmentException;

public sealed class ResolveFulfillmentExceptionCommandValidator : AbstractValidator<ResolveFulfillmentExceptionCommand>
{
    public ResolveFulfillmentExceptionCommandValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.IdempotencyKey).NotEmpty();
        RuleFor(c => c.Action).Must(a => a is "retry" or "cancel").WithMessage("action must be 'retry' or 'cancel'.");
    }
}
