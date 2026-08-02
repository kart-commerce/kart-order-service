using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KartOrderService.Infrastructure.Http;

/// <summary>
/// architecture.md's Compensation-Refund Trigger: Order authenticates to
/// `kart-payment-service`'s `POST /payments/{id}/refund` as the `orderServicePrincipal`
/// client-credentials principal (`payment-compensation` scope,
/// `kart-payment-service/contracts/api-contract.yaml`). Fetches and in-memory-caches a bearer token
/// from Identity's token endpoint until shortly before expiry — this requires a real
/// `Payment:ClientCredentials:{TokenUrl,ClientId,ClientSecret}` registration with Identity in any
/// environment where the refund call must actually succeed; that registration is an operational/
/// deployment concern, not something this code can supply.
/// </summary>
public sealed class ClientCredentialsTokenProvider(IHttpClientFactory httpClientFactory, IMemoryCache cache, IConfiguration configuration, ILogger<ClientCredentialsTokenProvider> logger)
{
    private const string CacheKey = "order-service-payment-compensation-token";

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out string? cached))
        {
            return cached;
        }

        var tokenUrl = configuration["Payment:ClientCredentials:TokenUrl"];
        var clientId = configuration["Payment:ClientCredentials:ClientId"];
        var clientSecret = configuration["Payment:ClientCredentials:ClientSecret"];

        if (string.IsNullOrEmpty(tokenUrl) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            logger.LogWarning("Payment:ClientCredentials is not fully configured; the compensation-refund call will be attempted unauthenticated.");
            return null;
        }

        using var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "payment-compensation",
        }), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Fetching Order's client-credentials token from {TokenUrl} failed with {StatusCode}.", tokenUrl, response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (body is null)
        {
            return null;
        }

        cache.Set(CacheKey, body.AccessToken, TimeSpan.FromSeconds(Math.Max(30, body.ExpiresIn - 60)));
        return body.AccessToken;
    }

    /// <summary>Standard OAuth2 client-credentials token response — snake_case wire fields, per RFC 6749 §5.1.</summary>
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
