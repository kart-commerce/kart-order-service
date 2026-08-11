using FluentAssertions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Models;
using KartOrderService.Application.Features.ListOrders;
using KartOrderService.Domain.Orders;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartOrderService.UnitTests.Features;

public sealed class ListOrdersQueryHandlerTests
{
    private readonly IOrderReadRepository _readRepository = Substitute.For<IOrderReadRepository>();

    private ListOrdersQueryHandler CreateHandler() => new(_readRepository, NullLogger<ListOrdersQueryHandler>.Instance);

    [Fact]
    public async Task Handle_ForwardsFilterAndWrapsResultInPage()
    {
        var summary = new OrderSummaryDto(Guid.NewGuid(), Guid.NewGuid(), "Paid", new MoneyDto(10m, "USD"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _readRepository.SearchAsync(Arg.Any<OrderSearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<OrderSummaryDto>)[summary], 42L));

        var query = new ListOrdersQuery(OrderStatus.Paid, null, null, null, Page: 2, PageSize: 25);
        var result = await CreateHandler().Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.TotalCount.Should().Be(42);
        result.Value.Page.Should().Be(2);
        result.Value.PageSize.Should().Be(25);

        await _readRepository.Received(1).SearchAsync(
            Arg.Is<OrderSearchFilter>(f => f.Status == OrderStatus.Paid && f.Page == 2 && f.PageSize == 25),
            Arg.Any<CancellationToken>());
    }
}
