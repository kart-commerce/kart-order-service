using System.Net;
using System.Net.Http.Headers;
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
///
/// Inventory & Stock Management flow fix: this client used to attach no bearer token at all, so
/// every real call 401'd against kart-inventory-service's `OrderServicePolicy` (found + fixed
/// 2026-08-12 — see [[kart-flow7-order-management-admin-done]] for the original discovery during
/// flow #7). Now shares `ClientCredentialsTokenProvider` with `PaymentClient`, scoped to its own
/// `Inventory:ClientCredentials` config section — degrades to sending unauthenticated (matching
/// `PaymentClient`'s own fallback) rather than failing closed if that section is unset.
/// </summary>
public sealed class InventoryClient(HttpClient httpClient, ClientCredentialsTokenProvider tokenProvider, ILogger<InventoryClient> logger) : IInventoryClient
{
    public async Task<InventoryReserveResult> ReserveAsync(Guid orderId, string sku, int qty, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/inventory/reserve")
            {
                Content = JsonContent.Create(new { orderId, sku, qty }),
            };

            var accessToken = await tokenProvider.GetAccessTokenAsync("Inventory", "inventory-reserve", cancellationToken);
            if (accessToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            var response = await httpClient.SendAsync(request, cancellationToken);

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
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/inventory/release")
            {
                Content = JsonContent.Create(new { reservationId }),
            };

            var accessToken = await tokenProvider.GetAccessTokenAsync("Inventory", "inventory-reserve", cancellationToken);
            if (accessToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            var response = await httpClient.SendAsync(request, cancellationToken);
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
