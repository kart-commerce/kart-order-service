using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Common.Behaviors;

/// <summary>Runs every registered `AbstractValidator&lt;TRequest&gt;` before the handler; throws FluentValidation's own `ValidationException`, which `Kart.Shared.ErrorHandling.KartExceptionHandler` special-cases to `400` with a per-field error map. Mirrors kart-payment-service's `ValidationBehavior`.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators,
    ILogger<ValidationBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
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
            var requestName = typeof(TRequest).Name;

            // checkpoint-logging-standard.md stage 4 ("<Rule>ValidationFailed", Warning with the
            // reason before throwing) generalized here for every FluentValidation validator on
            // this service, rather than duplicated per handler — the ValidationException itself
            // is still logged once more, generically, at the API boundary by
            // Kart.Shared.ErrorHandling.KartExceptionHandler; this line is the one that's
            // greppable by Stage and carries the actual field-level reasons.
            logger.LogWarning(
                "Stage {Stage}: {RequestName} rejected — {Errors}",
                $"{requestName}ValidationFailed",
                requestName,
                string.Join("; ", failures.Select(f => $"{f.PropertyName}: {f.ErrorMessage}")));

            throw new ValidationException(failures);
        }

        return await next();
    }
}
