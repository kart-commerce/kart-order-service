using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KartOrderService.Infrastructure.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KartOrderService.UnitTests.Infrastructure;

/// <summary>
/// Inventory & Stock Management flow's real end-to-end run found this the hard way: the token
/// endpoint always returned 200, yet GetAccessTokenAsync always returned null, so every real
/// caller (InventoryClient/PaymentClient) silently sent every downstream request unauthenticated.
/// Root cause: TokenResponse's JsonPropertyName attributes assumed RFC 6749 snake_case
/// (access_token/expires_in), but kart-identity-service's real response is camelCase
/// (accessToken/expiresIn) - confirmed live via curl against a real running identity-service.
/// This test asserts against that exact real wire shape so this exact regression can't silently
/// reappear (mirrors InventoryClientTests' own precedent for the same class of bug).
/// </summary>
public sealed class ClientCredentialsTokenProviderTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string ResponseJson { get; set; } = "{}";
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(ResponseStatusCode) { Content = JsonContent.Create(JsonSerializer.Deserialize<JsonElement>(ResponseJson)) };
            return Task.FromResult(response);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Inventory:ClientCredentials:TokenUrl"] = "http://identity/v1/auth/token",
            ["Inventory:ClientCredentials:ClientId"] = "order-service",
            ["Inventory:ClientCredentials:ClientSecret"] = "dev-order-service-client-secret",
        })
        .Build();

    [Fact]
    public async Task GetAccessTokenAsync_WithRealIdentityWireShape_ReturnsTheAccessToken()
    {
        // The exact real shape kart-identity-service's IssueServicePrincipalTokenCommandHandler
        // returns - camelCase, not RFC 6749's snake_case.
        var handler = new RecordingHandler
        {
            ResponseJson = """{"accessToken":"real-token-value","tokenType":"Bearer","expiresIn":900,"scopes":["inventory-reserve"]}""",
        };
        var provider = new ClientCredentialsTokenProvider(
            new SingleClientFactory(handler), new MemoryCache(new MemoryCacheOptions()), Configuration(), NullLogger<ClientCredentialsTokenProvider>.Instance);

        var token = await provider.GetAccessTokenAsync("Inventory", "inventory-reserve", CancellationToken.None);

        Assert.Equal("real-token-value", token);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithSnakeCaseWireShape_ReturnsNull()
    {
        // Guards the other direction too - if a future change reverts to expecting snake_case,
        // this documents that the real server never sends it, so silently "succeeding" here would
        // be the bug, not the fix.
        var handler = new RecordingHandler
        {
            ResponseJson = """{"access_token":"real-token-value","expires_in":900}""",
        };
        var provider = new ClientCredentialsTokenProvider(
            new SingleClientFactory(handler), new MemoryCache(new MemoryCacheOptions()), Configuration(), NullLogger<ClientCredentialsTokenProvider>.Instance);

        var token = await provider.GetAccessTokenAsync("Inventory", "inventory-reserve", CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetAccessTokenAsync_CachesTheTokenAcrossCalls()
    {
        var handler = new RecordingHandler
        {
            ResponseJson = """{"accessToken":"real-token-value","tokenType":"Bearer","expiresIn":900,"scopes":["inventory-reserve"]}""",
        };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new ClientCredentialsTokenProvider(new SingleClientFactory(handler), cache, Configuration(), NullLogger<ClientCredentialsTokenProvider>.Instance);

        await provider.GetAccessTokenAsync("Inventory", "inventory-reserve", CancellationToken.None);
        handler.ResponseJson = """{"accessToken":"a-different-token","tokenType":"Bearer","expiresIn":900,"scopes":["inventory-reserve"]}""";
        var second = await provider.GetAccessTokenAsync("Inventory", "inventory-reserve", CancellationToken.None);

        Assert.Equal("real-token-value", second);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenConfigSectionIsMissing_ReturnsNullWithoutCallingIdentity()
    {
        var handler = new RecordingHandler();
        var provider = new ClientCredentialsTokenProvider(
            new SingleClientFactory(handler), new MemoryCache(new MemoryCacheOptions()), new ConfigurationBuilder().Build(), NullLogger<ClientCredentialsTokenProvider>.Instance);

        var token = await provider.GetAccessTokenAsync("Inventory", "inventory-reserve", CancellationToken.None);

        Assert.Null(token);
        Assert.Null(handler.LastRequest);
    }
}
