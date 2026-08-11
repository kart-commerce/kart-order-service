using FluentValidation;

namespace KartOrderService.Application.Features.RequestShipment;

public sealed class RequestShipmentCommandValidator : AbstractValidator<RequestShipmentCommand>
{
    public RequestShipmentCommandValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.IdempotencyKey).NotEmpty();
    }
}
