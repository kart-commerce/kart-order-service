using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace KartOrderService.IntegrationTests;

/// <summary>
/// Flow #7 (Order Management, Admin) end-to-end against real Postgres + Mongo — the new admin list /
/// shipping-address / status / invoice / request-shipment endpoints, including their AdminOnly
/// authorization gate. Mirrors <see cref="CancelOrderEndpointTests"/>'s shape.
/// </summary>
public sealed class OrderManagementAdminEndpointTests : IClassFixture<OrderApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderApiFactory _factory;

    public OrderManagementAdminEndpointTests(OrderApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(Guid OrderId, Guid UserId)> CreateOrderAsync(HttpClient client)
    {
        var userId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                userId,
                items = new[] { new { sku = "SKU-1", qty = 1, unitPrice = new { amount = 20m, currency = "USD" } } },
                currency = "USD",
            }),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await response.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
        return (created!.OrderId, userId);
    }

    private static HttpRequestMessage AdminRequest(HttpMethod method, string uri, object? body = null, bool withIdempotencyKey = true)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Test-Roles", "admin");
        if (withIdempotencyKey)
        {
            request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static object SampleAddress() => new
    {
        recipientName = "Ada Lovelace",
        line1 = "1 Analytical Ave",
        line2 = (string?)null,
        city = "London",
        state = "LDN",
        postalCode = "EC1",
        country = "GB",
        phone = "+44 20 0000 0000",
    };

    // ── List ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_AsAdmin_EventuallyReturnsTheCreatedOrder()
    {
        var client = _factory.CreateClient();
        var (orderId, userId) = await CreateOrderAsync(client);

        PagedOrdersResponse? page = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/v1/orders?userId={userId}&page=1&pageSize=10", withIdempotencyKey: false));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            page = await response.Content.ReadFromJsonAsync<PagedOrdersResponse>(JsonOptions);
            if (page!.Items.Any(i => i.OrderId == orderId))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        page!.Items.Should().Contain(i => i.OrderId == orderId && i.Status == "Created");
        page.TotalCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task List_WithoutAdminRole_ReturnsForbidden()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/v1/orders?page=1&pageSize=10"); // authenticated as default customer

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_InvalidPageSize_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(HttpMethod.Get, "/v1/orders?page=1&pageSize=0", withIdempotencyKey: false));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Shipping address ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateShippingAddress_AsAdmin_ReturnsOkWithAddress_StatusUnchanged()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client);

        var response = await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/v1/orders/{orderId}/shipping-address", SampleAddress()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<OrderViewResponse>(JsonOptions);
        view!.Status.Should().Be("Created");
        view.ShippingAddress.Should().NotBeNull();
        view.ShippingAddress!.City.Should().Be("London");
    }

    [Fact]
    public async Task UpdateShippingAddress_WithoutAdminRole_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/v1/orders/{orderId}/shipping-address")
        {
            Content = JsonContent.Create(SampleAddress()),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateShippingAddress_UnknownOrder_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/v1/orders/{Guid.NewGuid()}/shipping-address", SampleAddress()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Status ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateStatus_IllegalTransition_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client); // Created — Created→Shipped is illegal

        var response = await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/v1/orders/{orderId}/status", new { targetStatus = "Shipped", reason = "manual" }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateStatus_InvalidTarget_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client);

        var response = await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/v1/orders/{orderId}/status", new { targetStatus = "Paid", reason = "manual" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateStatus_WithoutAdminRole_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/v1/orders/{orderId}/status")
        {
            Content = JsonContent.Create(new { targetStatus = "Shipped", reason = "manual" }),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Request shipment ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestShipment_WhenNotPaid_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client); // Created, not Paid

        var response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/v1/orders/{orderId}/request-shipment"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RequestShipment_WithoutAdminRole_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/orders/{orderId}/request-shipment");
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Invoice ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Invoice_CreatedOrder_EventuallyReturnsConflict()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client);

        // The invoice reads from the read model; wait until the order has projected, then confirm a
        // Created order (no completed payment) is a 409 rather than an invoiceable state.
        HttpStatusCode status = HttpStatusCode.NotFound;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/v1/orders/{orderId}/invoice", withIdempotencyKey: false));
            status = response.StatusCode;
            if (status != HttpStatusCode.NotFound)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        status.Should().Be(HttpStatusCode.Conflict, "a Created order has no completed payment to invoice");
    }

    [Fact]
    public async Task Invoice_WithoutAdminRole_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var (orderId, _) = await CreateOrderAsync(client);

        var response = await client.GetAsync($"/v1/orders/{orderId}/invoice"); // default customer

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Invoice_UnknownOrder_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/v1/orders/{Guid.NewGuid()}/invoice", withIdempotencyKey: false));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record OrderViewResponse(Guid OrderId, Guid UserId, string Status, ShippingAddressResponse? ShippingAddress);
    private sealed record ShippingAddressResponse(string RecipientName, string Line1, string City, string Country);
    private sealed record PagedOrdersResponse(List<OrderSummaryResponse> Items, long TotalCount, int Page, int PageSize);
    private sealed record OrderSummaryResponse(Guid OrderId, Guid UserId, string Status);
}
