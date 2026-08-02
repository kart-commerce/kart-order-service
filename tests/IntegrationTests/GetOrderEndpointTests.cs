using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace KartOrderService.IntegrationTests;

/// <summary>ORD-3/ORD-4 end-to-end: proves the real `OrderReadModelProjectorHostedService` actually keeps the Mongo read model in sync with what `POST /v1/orders` just committed to Postgres — not just that the projector's own unit-tested logic is correct in isolation.</summary>
public sealed class GetOrderEndpointTests : IClassFixture<OrderApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderApiFactory _factory;

    public GetOrderEndpointTests(OrderApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_AfterCreate_EventuallyReturnsTheProjectedOrder()
    {
        var client = _factory.CreateClient();
        var userId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                userId,
                items = new[] { new { sku = "SKU-1", qty = 2, unitPrice = new { amount = 15m, currency = "USD" } } },
                currency = "USD",
            }),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await response.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        OrderViewResponse? fetched = null;
        while (DateTime.UtcNow < deadline)
        {
            var getResponse = await client.GetAsync($"/v1/orders/{created!.OrderId}");
            if (getResponse.StatusCode == HttpStatusCode.OK)
            {
                fetched = await getResponse.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        fetched.Should().NotBeNull("the read-model projector should have caught up well within the poll window");
        fetched!.OrderId.Should().Be(created!.OrderId);
        fetched.Status.Should().Be("Created");
        fetched.Items.Should().ContainSingle(i => i.Sku == "SKU-1" && i.Qty == 2);
    }

    [Fact]
    public async Task Get_UnknownOrder_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/v1/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record OrderViewResponse(Guid OrderId, Guid UserId, string Status, List<OrderLineItemViewResponse> Items);
    private sealed record OrderLineItemViewResponse(string Sku, int Qty);
}
