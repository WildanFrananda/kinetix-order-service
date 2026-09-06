using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Moq;
using Xunit;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Tests;

public class CartServiceTests {
    private readonly Mock<IDistributedCache> _mockCache;
    private readonly CartService _cartService;

    public CartServiceTests() {
        _mockCache = new Mock<IDistributedCache>();
        _cartService = new CartService(_mockCache.Object);
    }

    [Fact]
    public async Task AddItemAsync_AddsNewItemToCart() {
        // Arrange
        string customerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
        _mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync((byte[]?)null);

        var request = new AddCartItemRequest("TSHIRT-BLK-M", "Kinetix Premium Shirt", 150000m, 2, "apparel");

        // Act
        var result = await _cartService.AddItemAsync(customerPrincipalId, request);

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("TSHIRT-BLK-M", result.Items[0].ProductId);
        Assert.Equal(2, result.Items[0].Quantity);
        Assert.Equal(300000m, result.Subtotal);
    }

    [Fact]
    public async Task RemoveItemAsync_RemovesItemFromCart() {
        // Arrange
        string customerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
        var existingCart = new Domain.Entities.CustomerCart(customerPrincipalId);
        existingCart.Items.Add(new Domain.Entities.CartItem {
            ProductId = "SHOES-RUN-42",
            ProductTitle = "Kinetix Running Shoes",
            UnitPrice = 500000m,
            Quantity = 1
        });

        var json = JsonSerializer.SerializeToUtf8Bytes(existingCart);
        _mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(json);

        // Act
        var result = await _cartService.RemoveItemAsync(customerPrincipalId, "SHOES-RUN-42");

        // Assert
        Assert.Empty(result.Items);
        Assert.Equal(0m, result.Subtotal);
    }
}
