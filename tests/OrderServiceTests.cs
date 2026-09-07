using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;
using OrderApplicationService = Kinetix.OrderService.Application.Services.OrderService;

namespace Kinetix.OrderService.Tests;

public class OrderServiceTests {
    private static OrderApplicationService NewOrderService(
        OrderDbContext db, ICartService cart, IPricingClient pricing) {

        var voucher = new Mock<IVoucherQuotaClient>();
        voucher.Setup(c => c.RedeemVoucherAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var flash = new Mock<IFlashSaleClient>();
        flash.Setup(c => c.AllocateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var stock = new Mock<IStockClient>();
        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ReturnsAsync(StepResult.Ok());

        var runner = new CheckoutSagaRunner(db, voucher.Object, flash.Object, stock.Object, escrow.Object,
            NullLogger<CheckoutSagaRunner>.Instance);

        return new OrderApplicationService(db, cart, pricing, runner);
    }
    private static OrderDbContext GetInMemoryDbContext() {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new OrderDbContext(options);
    }

    [Fact]
    public async Task CheckoutAsync_CreatesOrderAndClearsCart() {
        // Arrange
        using var dbContext = GetInMemoryDbContext();
        var mockCartService = new Mock<ICartService>();
        var mockPricingClient = new Mock<IPricingClient>();

        string customerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
        var cart = new CustomerCart(customerPrincipalId);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "Sample Product",
            UnitPrice = 100000m,
            Quantity = 2
        });

        mockCartService.Setup(s => s.GetCartAsync(customerPrincipalId))
            .ReturnsAsync(cart);

        mockPricingClient.Setup(p => p.CalculatePriceAsync("DISCOUNT10", It.IsAny<IReadOnlyList<PriceLine>>(), 15000m))
            .ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 15000m, 0m, 15000m, 195000m, []));

        var orderService = NewOrderService(dbContext, mockCartService.Object, mockPricingClient.Object);
        var request = new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", "DISCOUNT10", "KINETIX_INSTANT", 15000m, 5.2);

        // Act
        var result = await orderService.CheckoutAsync(customerPrincipalId, request, "IDEMP-KEY-12345");

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("ORD-", result.OrderNumber);
        Assert.Equal("PENDING_PAYMENT", result.Status);
        Assert.Equal(200000m, result.Subtotal);
        Assert.Equal(20000m, result.DiscountAmount);
        Assert.Equal(15000m, result.BaseShippingFee);
        Assert.Equal(195000m, result.FinalTotal);
        Assert.Equal("KINETIX_INSTANT", result.ShippingServiceTier);
        Assert.Equal(5.2, result.DistanceKm);

        mockCartService.Verify(s => s.ClearCartAsync(customerPrincipalId), Times.Once);
        mockPricingClient.Verify(p => p.CalculatePriceAsync("DISCOUNT10", It.IsAny<IReadOnlyList<PriceLine>>(), 15000m), Times.Once);
    }

    [Fact]
    public async Task TransitionOrderStatusAsync_ValidTransition_UpdatesStatus() {
        // Arrange
        using var dbContext = GetInMemoryDbContext();
        var mockCartService = new Mock<ICartService>();
        var mockPricingClient = new Mock<IPricingClient>();

        var order = new OrderEntity {
            OrderNumber = "ORD-20260815-001",
            CustomerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7",
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = 100000m,
            FinalTotal = 100000m,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta"
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();

        var orderService = NewOrderService(dbContext, mockCartService.Object, mockPricingClient.Object);

        // Act
        var result = await orderService.TransitionOrderStatusAsync(order.Id, OrderStatus.PAID);

        // Assert
        Assert.Equal("PAID", result.Status);
    }

    [Fact]
    public async Task TransitionOrderStatusAsync_InvalidTransition_ThrowsException() {
        // Arrange
        using var dbContext = GetInMemoryDbContext();
        var mockCartService = new Mock<ICartService>();
        var mockPricingClient = new Mock<IPricingClient>();

        var order = new OrderEntity {
            OrderNumber = "ORD-20260815-002",
            CustomerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7",
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = 100000m,
            FinalTotal = 100000m,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta"
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();

        var orderService = NewOrderService(dbContext, mockCartService.Object, mockPricingClient.Object);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orderService.TransitionOrderStatusAsync(order.Id, OrderStatus.DELIVERED));
    }
}
