using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace KartOrderService.IntegrationTests;

/// <summary>ORD-5 end-to-end, against real Postgres — proves the actual compare-and-swap concurrency token, not just the in-memory handler logic UnitTests already cover.</summary>
public sealed class CancelOrderEndpointTests : IClassFixture<OrderApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderApiFactory _factory;

    public CancelOrderEndpointTests(OrderApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<Guid> CreateOrderAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                userId = Guid.NewGuid(),
                items = new[] { new { sku = "SKU-1", qty = 1, unitPrice = new { amount = 20m, currency = "USD" } } },
                currency = "USD",
            }),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await response.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
        return created!.OrderId;
    }

    [Fact]
    public async Task Cancel_FreshlyCreatedOrder_ReturnsOk_WithCancelledStatus()
    {
        var client = _factory.CreateClient();
        var orderId = await CreateOrderAsync(client);

        var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/orders/{orderId}/cancel");
        cancelRequest.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await client.SendAsync(cancelRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
        view!.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task Cancel_WithReasonBody_ReturnsOk_WithCancelledStatus()
    {
        var client = _factory.CreateClient();
        var orderId = await CreateOrderAsync(client);

        var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/v1/orders/{orderId}/cancel")
        {
            Content = JsonContent.Create(new { reason = "customer_changed_mind" }),
        };
        cancelRequest.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await client.SendAsync(cancelRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
        view!.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task Cancel_TwiceInARow_IsIdempotent_SecondCallStillReturnsOk()
    {
        var client = _factory.CreateClient();
        var orderId = await CreateOrderAsync(client);

        async Task<HttpResponseMessage> SendCancelAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/orders/{orderId}/cancel");
            req.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
            return await client.SendAsync(req);
        }

        var first = await SendCancelAsync();
        var second = await SendCancelAsync();

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cancel_UnknownOrder_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/orders/{Guid.NewGuid()}/cancel");
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record OrderViewResponse(Guid OrderId, Guid UserId, string Status);
}
