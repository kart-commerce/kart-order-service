using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KartOrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KartOrderService.IntegrationTests;

/// <summary>ORD-1 end-to-end, against real Postgres - proves the actual unique-constraint-backed idempotency guard, not just the in-memory handler logic UnitTests already cover.</summary>
public sealed class CreateOrderEndpointTests : IClassFixture<OrderApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderApiFactory _factory;

    public CreateOrderEndpointTests(OrderApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private static HttpRequestMessage CreateRequest(Guid userId, string sku, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                userId,
                items = new[] { new { sku, qty = 1, unitPrice = new { amount = 20m, currency = "USD" } } },
                currency = "USD",
            }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsAccepted_WithCreatedStatus()
    {
        var response = await CreateClient().SendAsync(CreateRequest(Guid.NewGuid(), "SKU-1", $"key-{Guid.NewGuid():N}"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var view = await response.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
        view!.Status.Should().Be("Created");
    }

    [Fact]
    public async Task Create_SameIdempotencyKeySameBody_Twice_ReturnsIdenticalOrder_NoSecondRow()
    {
        var userId = Guid.NewGuid();
        var idempotencyKey = $"key-{Guid.NewGuid():N}";

        var first = await CreateClient().SendAsync(CreateRequest(userId, "SKU-1", idempotencyKey));
        var second = await CreateClient().SendAsync(CreateRequest(userId, "SKU-1", idempotencyKey));

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var firstView = await first.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
        var secondView = await second.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
        secondView!.OrderId.Should().Be(firstView!.OrderId, "the replayed request must return the exact same order, never a second one");

        await AssertExactlyOneOrderForKeyAsync(idempotencyKey);
    }

    [Fact]
    public async Task Create_SameIdempotencyKeyDifferentBody_ReturnsUnprocessableEntity()
    {
        var idempotencyKey = $"key-{Guid.NewGuid():N}";

        var first = await CreateClient().SendAsync(CreateRequest(Guid.NewGuid(), "SKU-1", idempotencyKey));
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var second = await CreateClient().SendAsync(CreateRequest(Guid.NewGuid(), "SKU-2", idempotencyKey));

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Create_OutOfStockSku_ReturnsConflict_NoOrderPersisted()
    {
        var idempotencyKey = $"key-{Guid.NewGuid():N}";

        var response = await CreateClient().SendAsync(CreateRequest(Guid.NewGuid(), "SKU-OUT-OF-STOCK", idempotencyKey));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertNoOrderForKeyAsync(idempotencyKey);
    }

    [Fact]
    public async Task Create_ConcurrentRequestsWithTheSameIdempotencyKey_NeverProduceTwoOrders()
    {
        var userId = Guid.NewGuid();
        var idempotencyKey = $"key-{Guid.NewGuid():N}";

        // 10 concurrent requests racing on the exact same Idempotency-Key - the real-world shape of
        // a client retrying after a timeout while the original request is still in flight.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => CreateClient().SendAsync(CreateRequest(userId, "SKU-1", idempotencyKey))));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted, "every racing request with an identical payload must be served as a safe replay, never an error");

        await AssertExactlyOneOrderForKeyAsync(idempotencyKey);
    }

    private async Task AssertExactlyOneOrderForKeyAsync(string idempotencyKey)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var count = await dbContext.Orders.CountAsync(o => o.IdempotencyKey == idempotencyKey);
        count.Should().Be(1, "idx_orders_idempotency_key must guarantee exactly one order per key");
    }

    private async Task AssertNoOrderForKeyAsync(string idempotencyKey)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var count = await dbContext.Orders.CountAsync(o => o.IdempotencyKey == idempotencyKey);
        count.Should().Be(0, "no order should persist past a failed synchronous Inventory reserve call");
    }

    private sealed record OrderViewResponse(Guid OrderId, Guid UserId, string Status);
}
