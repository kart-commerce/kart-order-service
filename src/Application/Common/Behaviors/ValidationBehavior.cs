using FluentValidation;
using MediatR;

namespace KartOrderService.Application.Common.Behaviors;

/// <summary>Runs every registered `AbstractValidator&lt;TRequest&gt;` before the handler; throws FluentValidation's own `ValidationException`, which `Kart.Shared.ErrorHandling.KartExceptionHandler` special-cases to `400` with a per-field error map. Mirrors kart-payment-service's `ValidationBehavior`.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = (await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, cancellationToken))))
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToList();

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return await next();
    }
}
