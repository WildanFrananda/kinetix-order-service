using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs;
using Kinetix.OrderService.Infrastructure.Persistence;

namespace Kinetix.OrderService.Tests;

public class OrderServiceTests {
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

        long customerId = 1001;
        var cart = new CustomerCart(customerId);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "Sample Product",
            UnitPrice = 100000m,
            Quantity = 2
        });

        mockCartService.Setup(s => s.GetCartAsync(customerId))
            .ReturnsAsync(cart);

        mockPricingClient.Setup(p => p.CalculatePriceAsync("DISCOUNT10", 200000m, 15000m))
            .ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 15000m, 0m, 15000m, 195000m));

        var orderService = new Application.Services.OrderService(dbContext, mockCartService.Object, mockPricingClient.Object);
        var request = new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", "DISCOUNT10", "KINETIX_INSTANT", 15000m, 5.2);

        // Act
        var result = await orderService.CheckoutAsync(customerId, request, "IDEMP-KEY-12345");

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

        mockCartService.Verify(s => s.ClearCartAsync(customerId), Times.Once);
        mockPricingClient.Verify(p => p.CalculatePriceAsync("DISCOUNT10", 200000m, 15000m), Times.Once);
    }

    [Fact]
    public async Task TransitionOrderStatusAsync_ValidTransition_UpdatesStatus() {
        // Arrange
        using var dbContext = GetInMemoryDbContext();
        var mockCartService = new Mock<ICartService>();
        var mockPricingClient = new Mock<IPricingClient>();

        var order = new Order {
            OrderNumber = "ORD-20260815-001",
            CustomerId = 1001,
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = 100000m,
            FinalTotal = 100000m,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta"
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();

        var orderService = new Application.Services.OrderService(dbContext, mockCartService.Object, mockPricingClient.Object);

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

        var order = new Order {
            OrderNumber = "ORD-20260815-002",
            CustomerId = 1001,
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = 100000m,
            FinalTotal = 100000m,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta"
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();

        var orderService = new Application.Services.OrderService(dbContext, mockCartService.Object, mockPricingClient.Object);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orderService.TransitionOrderStatusAsync(order.Id, OrderStatus.DELIVERED));
    }
}
