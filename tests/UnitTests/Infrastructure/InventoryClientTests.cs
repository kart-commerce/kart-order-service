using System.Net;
using System.Net.Http.Json;
using KartOrderService.Infrastructure.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KartOrderService.UnitTests.Infrastructure;

/// <summary>
/// Order Management (Admin) flow #7's real end-to-end run found this the hard way: ReserveAsync/
/// ReleaseAsync posted to "/inventory/reserve"/"/inventory/release" (no "/v1" prefix), but
/// kart-inventory-service's own InventoryController is routed at "v1/inventory" — every real
/// CreateOrder call has therefore always 404'd its synchronous reserve call in the actual deployed
/// stack (masked as a generic "Unavailable" outcome, never previously caught because every
/// existing integration test stands in a FakeInventoryClient rather than exercising the real HTTP
/// client's request shape). These tests assert the exact request URI so this exact regression
/// can't silently reappear.
/// </summary>
public sealed class InventoryClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.Created;
        public object? ResponseBody { get; set; } = new { reservationId = Guid.NewGuid() };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(ResponseStatusCode);
            if (ResponseBody is not null)
            {
                response.Content = JsonContent.Create(ResponseBody);
            }
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ReserveAsync_PostsToTheVersionedInventoryReservePath()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://inventory:8080") };
        var client = new InventoryClient(httpClient, NullLogger<InventoryClient>.Instance);

        await client.ReserveAsync(Guid.NewGuid(), "SKU-1", 1, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/v1/inventory/reserve", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ReleaseAsync_PostsToTheVersionedInventoryReleasePath()
    {
        var handler = new RecordingHandler { ResponseStatusCode = HttpStatusCode.OK, ResponseBody = null };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://inventory:8080") };
        var client = new InventoryClient(httpClient, NullLogger<InventoryClient>.Instance);

        await client.ReleaseAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/v1/inventory/release", handler.LastRequest!.RequestUri!.AbsolutePath);
    }
}
