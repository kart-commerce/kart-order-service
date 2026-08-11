using System.Net;
using System.Net.Http.Json;
using KartOrderService.IntegrationTests;
using Xunit;

namespace KartOrderService.ContractTests;

/// <summary>Verifies ORD-1/4/5/12 against `contracts/api-contract.yaml`'s four `/v1/orders` paths — both the contract's own structural shape and a live smoke check per path.</summary>
public sealed class OrdersContractTests : IClassFixture<OrderApiFactory>
{
    private readonly OrderApiFactory _factory;

    public OrdersContractTests(OrderApiFactory factory) => _factory = factory;

    [Fact]
    public void Contract_DefinesCreateOrderPath_WithExpectedResponses()
    {
        var contract = ContractLoader.Load();
        var paths = (Dictionary<object, object>)contract["paths"];
        Assert.True(paths.ContainsKey("/v1/orders"), "api-contract.yaml no longer defines /v1/orders");

        var postOp = (Dictionary<object, object>)((Dictionary<object, object>)paths["/v1/orders"])["post"];
        Assert.Equal("createOrder", postOp["operationId"]);

        var responses = (Dictionary<object, object>)postOp["responses"];
        Assert.True(responses.ContainsKey("202"));
        Assert.True(responses.ContainsKey("409"));
        Assert.True(responses.ContainsKey("422"));
        Assert.True(responses.ContainsKey("503"));
    }

    [Fact]
    public void Contract_DefinesGetAndCancelPaths()
    {
        var contract = ContractLoader.Load();
        var paths = (Dictionary<object, object>)contract["paths"];

        Assert.True(paths.ContainsKey("/v1/orders/{id}"));
        Assert.True(paths.ContainsKey("/v1/orders/{id}/cancel"));
        Assert.True(paths.ContainsKey("/v1/orders/{id}/resolve-fulfillment-exception"));
    }

    [Fact]
    public void Contract_DefinesFlow7AdminPaths_WithExpectedOperations()
    {
        var contract = ContractLoader.Load();
        var paths = (Dictionary<object, object>)contract["paths"];

        // GET /v1/orders (admin list) added alongside the existing POST.
        var ordersPath = (Dictionary<object, object>)paths["/v1/orders"];
        Assert.True(ordersPath.ContainsKey("get"));
        Assert.Equal("listOrders", ((Dictionary<object, object>)ordersPath["get"])["operationId"]);

        Assert.True(paths.ContainsKey("/v1/orders/{id}/shipping-address"));
        Assert.True(paths.ContainsKey("/v1/orders/{id}/status"));
        Assert.True(paths.ContainsKey("/v1/orders/{id}/invoice"));
        Assert.True(paths.ContainsKey("/v1/orders/{id}/request-shipment"));

        Assert.True(((Dictionary<object, object>)paths["/v1/orders/{id}/shipping-address"]).ContainsKey("patch"));
        Assert.True(((Dictionary<object, object>)paths["/v1/orders/{id}/status"]).ContainsKey("patch"));
        Assert.True(((Dictionary<object, object>)paths["/v1/orders/{id}/invoice"]).ContainsKey("get"));
        Assert.True(((Dictionary<object, object>)paths["/v1/orders/{id}/request-shipment"]).ContainsKey("post"));
    }

    [Fact]
    public void Contract_OrderView_IncludesNullableShippingAddress_AndCancelRequestHasReason()
    {
        var contract = ContractLoader.Load();
        var schemas = (Dictionary<object, object>)((Dictionary<object, object>)contract["components"])["schemas"];

        var orderView = (Dictionary<object, object>)schemas["OrderView"];
        var orderViewProps = (Dictionary<object, object>)orderView["properties"];
        Assert.True(orderViewProps.ContainsKey("shippingAddress"));

        Assert.True(schemas.ContainsKey("ShippingAddress"));
        Assert.True(schemas.ContainsKey("PagedOrders"));
        Assert.True(schemas.ContainsKey("Invoice"));

        var cancelReq = (Dictionary<object, object>)schemas["CancelOrderRequest"];
        var cancelReqProps = (Dictionary<object, object>)cancelReq["properties"];
        Assert.True(cancelReqProps.ContainsKey("reason"));
    }

    [Fact]
    public async Task LiveEndpoint_ListOrders_WithoutAdminRole_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/orders?page=1&pageSize=10");
        request.Headers.Add("X-Test-Roles", "customer"); // authenticated, but not admin

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LiveEndpoint_CreateOrder_MissingIdempotencyKey_DoesNotCrash()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                userId = Guid.NewGuid(),
                items = new[] { new { sku = "SKU-1", qty = 1, unitPrice = new { amount = 10m, currency = "USD" } } },
                currency = "USD",
            }),
        };
        // Deliberately no Idempotency-Key header.

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task LiveEndpoint_ResolveFulfillmentException_WithoutAdminRole_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/orders/{Guid.NewGuid()}/resolve-fulfillment-exception")
        {
            Content = JsonContent.Create(new { action = "retry" }),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        request.Headers.Add("X-Test-Roles", "customer"); // authenticated, but not admin

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LiveEndpoint_GetUnknownOrder_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/v1/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
