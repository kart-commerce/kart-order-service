using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace KartOrderService.Api.Security;

/// <summary>
/// Resolves RS256 signing keys for validating an Identity-issued access token. Identity exposes its
/// public keys at `GET /.well-known/jwks.json`; this fetches and caches that JWKS document
/// directly. JwtBearer's `IssuerSigningKeyResolver` delegate is synchronous, so the in-memory cache
/// keeps the blocking fetch to once per <see cref="CacheDuration"/>. Mirrors
/// `kart-payment-service`'s identically-shaped resolver.
/// </summary>
public sealed class JwksSigningKeyResolver
{
    private const string CacheKey = "identity-jwks";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly string _jwksUri;

    public JwksSigningKeyResolver(HttpClient httpClient, IMemoryCache cache, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _cache = cache;
        _jwksUri = configuration["Identity:JwksUri"]
            ?? throw new InvalidOperationException("Identity:JwksUri is not configured.");
    }

    public IEnumerable<SecurityKey> ResolveSigningKeys(
        string token,
        SecurityToken securityToken,
        string kid,
        TokenValidationParameters validationParameters)
    {
        var keySet = _cache.GetOrCreate(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return FetchJwksAsync().GetAwaiter().GetResult();
        });

        return keySet?.Keys ?? Enumerable.Empty<SecurityKey>();
    }

    private async Task<JsonWebKeySet> FetchJwksAsync()
    {
        var response = await _httpClient.GetAsync(_jwksUri);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return new JsonWebKeySet(json);
    }
}
