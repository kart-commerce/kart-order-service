using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Application.Common.Behaviors;

/// <summary>Logs `{RequestName} completed in {ElapsedMilliseconds}ms` for every command/query/Saga-consumer command - message-template form only (observability-standards.md), never string interpolation.</summary>
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await next();
        }
        finally
        {
            logger.LogInformation("{RequestName} completed in {ElapsedMilliseconds}ms", requestName, stopwatch.ElapsedMilliseconds);
        }
    }
}
