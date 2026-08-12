using FluentAssertions;
using KartOrderService.Application.Common.Interfaces;
using KartOrderService.Application.Common.Models;
using KartOrderService.Application.Features.GenerateInvoice;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace KartOrderService.UnitTests.Features;

public sealed class GenerateInvoiceQueryHandlerTests
{
    private readonly IOrderReadRepository _readRepository = Substitute.For<IOrderReadRepository>();

    private GenerateInvoiceQueryHandler CreateHandler() => new(_readRepository, NullLogger<GenerateInvoiceQueryHandler>.Instance);

    private static OrderViewDto View(Guid orderId, string status) => new(
        orderId,
        Guid.NewGuid(),
        status,
        [new OrderLineItemViewDto("SKU-1", 2, new MoneyDto(10m, "USD"))],
        new MoneyDto(20m, "USD"),
        DateTimeOffset.UtcNow,
        null);

    [Fact]
    public async Task Handle_OrderNotFound_ReturnsNotFound()
    {
        _readRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((OrderViewDto?)null);

        var result = await CreateHandler().Handle(new GenerateInvoiceQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Handle_PaidOrder_ProducesDeterministicInvoice_SubtotalEqualsTotal()
    {
        var orderId = Guid.NewGuid();
        _readRepository.GetByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(View(orderId, "Paid"));

        var result = await CreateHandler().Handle(new GenerateInvoiceQuery(orderId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.InvoiceNumber.Should().Be("INV-" + orderId.ToString("N")[..10].ToUpperInvariant());
        result.Value.Subtotal.Should().BeEquivalentTo(result.Value.Total);
        result.Value.Items.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Reserved")]
    [InlineData("Cancelled")]
    public async Task Handle_OrderWithoutCompletedPayment_ReturnsConflict(string status)
    {
        var orderId = Guid.NewGuid();
        _readRepository.GetByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(View(orderId, status));

        var result = await CreateHandler().Handle(new GenerateInvoiceQuery(orderId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("conflict");
    }
}
