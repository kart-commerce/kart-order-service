using KartOrderService.Application.Common.Compensation;
using KartOrderService.Application.Common.Exceptions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Domain;
using KartOrderService.Domain.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Infrastructure.ReconciliationSweep;

/// <summary>
/// ORD-14: ddd-model.md Modeling Decision #6 — "an application service, not a domain aggregate...
/// it reads Order rows past their per-step staleness threshold and drives the same compensation
/// transitions any other trigger would, through the same Order aggregate." Runs every 60s
/// (design-decisions.md); thresholds are 2× each Saga step's own timeout budget (a grace margin
/// over the in-process handler, which should already have reacted by 1×): 4s awaiting
/// `InventoryReserved` (`Created`), 60s awaiting `PaymentCompleted`/`PaymentFailed` (`Reserved`),
/// 120s awaiting `ShipmentDispatched` (`Paid`). The first two force the same reverse-order
/// compensation ORD-8 uses; the Shipping-await case forces `FulfillmentException` instead (ORD-11),
/// never automatic refund-and-cancel, since `PaymentCompleted` has already been received by then.
/// </summary>
public sealed class ReconciliationSweepHostedService(IServiceScopeFactory scopeFactory, ILogger<ReconciliationSweepHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InventoryAwaitThreshold = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PaymentAwaitThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShippingAwaitThreshold = TimeSpan.FromSeconds(120);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconciliation sweep failed; retrying next interval.");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }
    }

    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var orderRepository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var compensator = scope.ServiceProvider.GetRequiredService<InventoryReleaseCompensator>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = timeProvider.GetUtcNow();

        var awaitingInventory = await orderRepository.GetStuckAsync(OrderStatus.Created, now - InventoryAwaitThreshold, stoppingToken);
        foreach (var order in awaitingInventory)
        {
            await CompensateAsync(order, unitOfWork, compensator, now, stoppingToken);
        }

        var awaitingPayment = await orderRepository.GetStuckAsync(OrderStatus.Reserved, now - PaymentAwaitThreshold, stoppingToken);
        foreach (var order in awaitingPayment)
        {
            await CompensateAsync(order, unitOfWork, compensator, now, stoppingToken);
        }

        var awaitingShipping = await orderRepository.GetStuckAsync(OrderStatus.Paid, now - ShippingAwaitThreshold, stoppingToken);
        foreach (var order in awaitingShipping)
        {
            await EnterFulfillmentExceptionAsync(order, unitOfWork, now, stoppingToken);
        }
    }

    private async Task CompensateAsync(Order order, IUnitOfWork unitOfWork, InventoryReleaseCompensator compensator, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await unitOfWork.BeginPrincipalScopedTransactionAsync(SystemPrincipals.ReconciliationSweep, "system", cancellationToken);

        order.RecordCompensationTriggered("reconciliation_sweep_timeout", SystemPrincipals.ReconciliationSweep, now);
        await compensator.ReleaseAllAsync(order, cancellationToken);

        var result = order.TryCancel("reconciliation_sweep_timeout", SystemPrincipals.ReconciliationSweep, now);
        if (result.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Reconciliation sweep could not cancel stuck order {OrderId}: {Reason}.", order.OrderId, result.Error.Message);
            return;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
            logger.LogWarning("Reconciliation sweep force-cancelled stuck order {OrderId}.", order.OrderId);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogInformation("Reconciliation sweep's compensation for order {OrderId} lost a concurrent race; a normal Saga-step consumer already moved it.", order.OrderId);
        }
    }

    private async Task EnterFulfillmentExceptionAsync(Order order, IUnitOfWork unitOfWork, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await unitOfWork.BeginPrincipalScopedTransactionAsync(SystemPrincipals.ReconciliationSweep, "system", cancellationToken);

        var result = order.TryEnterFulfillmentException(SystemPrincipals.ReconciliationSweep, now);
        if (result.IsFailure)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogWarning("Reconciliation sweep could not enter FulfillmentException for stuck order {OrderId}: {Reason}.", order.OrderId, result.Error.Message);
            return;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
            logger.LogWarning("Reconciliation sweep forced order {OrderId} into FulfillmentException after a silent Shipping timeout.", order.OrderId);
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            logger.LogInformation("Reconciliation sweep's FulfillmentException entry for order {OrderId} lost a concurrent race; a normal Saga-step consumer already moved it.", order.OrderId);
        }
    }
}
