using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KartOrderService.Domain.Orders;
using KartOrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KartOrderService.IntegrationTests;

/// <summary>
/// Order Management (Admin) flow #7's real end-to-end run found this: two events for the *same*
/// order sharing an identical `CreatedAt` (a real bulk-seeding-tool artifact, but not structurally
/// impossible for genuinely fast real transitions either) used to leave OrderCreated's own
/// SetOnInsert-based upsert at the mercy of EF Core's unspecified owned-collection load order —
/// if a later same-timestamp event's plain `$set` upsert ran first, it silently created a bare
/// `{_id, Status, UpdatedAt}` Mongo document, and OrderCreated's own SetOnInsert fields
/// (UserId/Items/TotalAmount/CreatedAt) were then permanently skipped since SetOnInsert is a
/// no-op once the document already exists. Fixed by adding `.ThenBy(e => e.Sequence)` to both
/// `OrderReadModelProjectorHostedService` and `OutboxRelayHostedService`'s own event ordering.
/// </summary>
public sealed class OrderReadModelProjectorOrderingTests : IClassFixture<OrderApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderApiFactory _factory;

    public OrderReadModelProjectorOrderingTests(OrderApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_WhenAShippingAddressUpdateSharesOrderCreatedsExactTimestamp_StillReturnsTheFullOrder()
    {
        var client = _factory.CreateClient();
        var userId = Guid.NewGuid();

        var createResponse = await PostCreateAsync(client, userId);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await createResponse.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);

        // Force a second event (OrderShippingAddressUpdated, Sequence 2) to share OrderCreated's
        // (Sequence 1) exact CreatedAt — the real-world collision condition — by passing that same
        // timestamp straight into the domain method rather than letting DateTimeOffset.UtcNow tick
        // forward naturally.
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var order = await dbContext.Orders.SingleAsync(o => o.OrderId == created!.OrderId);
            var orderCreatedAt = order.Events.Single(e => e.EventType == "OrderCreated").CreatedAt;

            var address = new ShippingAddress("Jane Doe", "1 Test St", null, "Testville", "TS", "00000", "US", null);
            order.UpdateShippingAddress(address, "test", orderCreatedAt).IsSuccess.Should().BeTrue();
            await dbContext.SaveChangesAsync();
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        OrderViewResponse? fetched = null;
        while (DateTime.UtcNow < deadline)
        {
            var getResponse = await client.GetAsync($"/v1/orders/{created!.OrderId}");
            if (getResponse.StatusCode == HttpStatusCode.OK)
            {
                var candidate = await getResponse.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
                if (candidate!.UserId != Guid.Empty)
                {
                    fetched = candidate;
                    break;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        fetched.Should().NotBeNull("OrderCreated's own fields must survive even though a same-timestamp later event exists");
        fetched!.UserId.Should().Be(userId);
        fetched.Items.Should().ContainSingle(i => i.Sku == "SKU-1" && i.Qty == 2);
        fetched.TotalAmount.Amount.Should().Be(30m);
    }

    private static Task<HttpResponseMessage> PostCreateAsync(HttpClient client, Guid userId)
    {
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
        return client.SendAsync(request);
    }

    private sealed record OrderViewResponse(Guid OrderId, Guid UserId, string Status, List<OrderLineItemViewResponse> Items, MoneyResponse TotalAmount);
    private sealed record OrderLineItemViewResponse(string Sku, int Qty);
    private sealed record MoneyResponse(decimal Amount, string Currency);
}
