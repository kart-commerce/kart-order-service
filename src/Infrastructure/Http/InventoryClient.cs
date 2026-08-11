using System.Net;
using System.Net.Http.Json;
using KartOrderService.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace KartOrderService.Infrastructure.Http;

/// <summary>
/// architecture.md's one genuinely synchronous outbound edge — `POST /inventory/reserve`/
/// `.../release`, per `kart-inventory-service/contracts/api-contract.yaml` (one `(sku, qty)` per
/// reserve call, never a whole order — `contracts/README.md` addendum #1). The 2s timeout +
/// circuit breaker (design-decisions.md) are applied to the registered named `HttpClient` itself
/// (`Infrastructure/DependencyInjection.cs`, via `Microsoft.Extensions.Http.Polly`) — this class
/// only translates HTTP responses/timeouts/broken-circuit exceptions into
/// <see cref="InventoryReserveResult"/>, never throwing for an expected outcome.
/// </summary>
public sealed class InventoryClient(HttpClient httpClient, ILogger<InventoryClient> logger) : IInventoryClient
{
    public async Task<InventoryReserveResult> ReserveAsync(Guid orderId, string sku, int qty, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("/v1/inventory/reserve", new { orderId, sku, qty }, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                var body = await response.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken);
                return new InventoryReserveResult(InventoryReserveOutcome.Reserved, body?.ReservationId);
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return new InventoryReserveResult(InventoryReserveOutcome.InsufficientStock, null);
            }

            logger.LogWarning("Inventory reserve for order {OrderId}/sku {Sku} returned unexpected status {StatusCode}.", orderId, sku, response.StatusCode);
            return new InventoryReserveResult(InventoryReserveOutcome.Unavailable, null);
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException or HttpRequestException or BrokenCircuitException)
        {
            logger.LogWarning(ex, "Inventory reserve for order {OrderId}/sku {Sku} timed out or its circuit breaker is open.", orderId, sku);
            return new InventoryReserveResult(InventoryReserveOutcome.Unavailable, null);
        }
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("/v1/inventory/release", new { reservationId }, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                logger.LogWarning("Inventory release for reservation {ReservationId} returned {StatusCode}.", reservationId, response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException or HttpRequestException or BrokenCircuitException)
        {
            // Best-effort here: a release that never lands still self-heals via Inventory's own
            // reservation TTL sweep (kart-inventory-service/ddd-model.md) - never blocks the
            // caller's own state transition on a transient Inventory outage.
            logger.LogWarning(ex, "Inventory release for reservation {ReservationId} failed transiently; relying on Inventory's own TTL sweep as the backstop.", reservationId);
        }
    }

    private sealed record ReservationResponse(Guid ReservationId);
}
