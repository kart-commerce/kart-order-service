using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Infrastructure.Http;

/// <summary>
/// Fetches and in-memory-caches an Order-Service-issued client-credentials bearer token from
/// Identity's token endpoint until shortly before expiry, for whichever downstream service needs
/// one — originally hardcoded to Payment's own Compensation-Refund Trigger (architecture.md:
/// Order authenticates to `kart-payment-service`'s `POST /payments/{id}/refund` as the
/// `orderServicePrincipal` client-credentials principal, `payment-compensation` scope), now
/// generalized (Inventory & Stock Management flow) so <c>InventoryClient</c> can share the exact
/// same pattern for `POST /inventory/reserve`/`.../release` instead of sending those calls
/// unauthenticated. <paramref name="configSection"/> selects the config block
/// (`{configSection}:ClientCredentials:{TokenUrl,ClientId,ClientSecret}`, e.g. `"Payment"` or
/// `"Inventory"`) — each downstream service is provisioned as its own Identity service principal,
/// not one shared credential reused everywhere. Requires a real registration with Identity (and a
/// matching `ServicePrincipalSeeds` entry) in any environment where the call must actually
/// succeed; that registration is an operational/deployment concern, not something this code can
/// supply — this class degrades to attempting the call unauthenticated (returns null) rather than
/// throwing when the config section is absent/incomplete or Identity rejects the request.
/// </summary>
public sealed class ClientCredentialsTokenProvider(IHttpClientFactory httpClientFactory, IMemoryCache cache, IConfiguration configuration, ILogger<ClientCredentialsTokenProvider> logger)
{
    public async Task<string?> GetAccessTokenAsync(string configSection, string scope, CancellationToken cancellationToken)
    {
        var cacheKey = $"order-service-{configSection.ToLowerInvariant()}-token";
        if (cache.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        var tokenUrl = configuration[$"{configSection}:ClientCredentials:TokenUrl"];
        var clientId = configuration[$"{configSection}:ClientCredentials:ClientId"];
        var clientSecret = configuration[$"{configSection}:ClientCredentials:ClientSecret"];

        if (string.IsNullOrEmpty(tokenUrl) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            logger.LogWarning("{ConfigSection}:ClientCredentials is not fully configured; the call will be attempted unauthenticated.", configSection);
            return null;
        }

        using var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope,
        }), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Fetching Order's {ConfigSection} client-credentials token from {TokenUrl} failed with {StatusCode}.", configSection, tokenUrl, response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (body?.AccessToken is null)
        {
            logger.LogWarning("Order's {ConfigSection} client-credentials token response from {TokenUrl} did not deserialize an access token.", configSection, tokenUrl);
            return null;
        }

        cache.Set(cacheKey, body.AccessToken, TimeSpan.FromSeconds(Math.Max(30, body.ExpiresIn - 60)));
        return body.AccessToken;
    }

    /// <summary>
    /// Inventory & Stock Management flow fix (2026-08-12): this record's JsonPropertyName
    /// attributes previously assumed RFC 6749 §5.1's snake_case (`access_token`/`expires_in`), but
    /// kart-identity-service's real `IssueServicePrincipalTokenCommandHandler`/
    /// `JwtAccessTokenGenerator` response is camelCase (`accessToken`/`expiresIn`) - confirmed live
    /// via `curl -X POST http://localhost:8081/v1/auth/token ...` returning
    /// `{"accessToken":"...","tokenType":"Bearer","expiresIn":900,"scopes":[...]}`. The mismatch
    /// silently deserialized AccessToken to null (System.Text.Json defaults an unmatched
    /// positional record parameter rather than throwing) - GetAccessTokenAsync then returned null
    /// even though the token endpoint itself always returned 200, so every real caller
    /// (InventoryClient, and PaymentClient before it) silently sent every request unauthenticated.
    /// Found live during this flow's own end-to-end test (a real POST /v1/orders 401'd against
    /// Inventory's OrderServicePolicy despite a 200 token response) - never caught by any existing
    /// test because none of them exercise a real Identity-issued token shape.
    /// </summary>
    private sealed record TokenResponse(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);
}
