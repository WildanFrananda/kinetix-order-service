using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Moq;
using Xunit;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Products;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.DTOs.Requests;

namespace Kinetix.OrderService.Tests;

public class CartServiceTests {
    private readonly Mock<IDistributedCache> _mockCache;
    private readonly Mock<IProductDirectory> _catalog;
    private readonly CartService _cartService;

    private static readonly CatalogProduct Shirt = new(
        ProductId: "TSHIRT-BLK-M",
        MerchantPrincipalId: "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
        Title: "Kinetix Premium Shirt",
        UnitPrice: 150000m,
        CategoryId: "apparel"
    );

    public CartServiceTests() {
        _mockCache = new Mock<IDistributedCache>();
        _catalog = new Mock<IProductDirectory>();
        _catalog.Setup(c => c.GetProductAsync(Shirt.ProductId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Shirt);
        _cartService = new CartService(_mockCache.Object, _catalog.Object);
    }

    [Fact]
    public async Task AddItemAsync_AddsNewItemToCart() {
        // Arrange
        string customerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
        _mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync((byte[]?)null);

        var request = new AddCartItemRequest("TSHIRT-BLK-M", 2);

        // Act
        var result = await _cartService.AddItemAsync(customerPrincipalId, request);

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("TSHIRT-BLK-M", result.Items[0].ProductId);
        Assert.Equal(2, result.Items[0].Quantity);
        Assert.Equal(300000m, result.Subtotal);

        Assert.Equal(150000m, result.Items[0].UnitPrice);
        Assert.Equal("Kinetix Premium Shirt", result.Items[0].ProductTitle);
        Assert.Equal("apparel", result.Items[0].CategoryId);
        Assert.Equal("3aa957c8-b802-4d58-b9fc-f7b76ce60fa3", result.Items[0].MerchantPrincipalId);
    }

    [Fact]
    public async Task AddItemAsync_WhenCatalogHasNoSuchProduct_RefusesRatherThanCarryingTheBuyersWord() {
        string customerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
        _mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync((byte[]?)null);
        _catalog.Setup(c => c.GetProductAsync("GHOST-01", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProductNotInCatalogException("GHOST-01"));

        await Assert.ThrowsAsync<ProductNotInCatalogException>(() =>
            _cartService.AddItemAsync(customerPrincipalId, new AddCartItemRequest("GHOST-01", 1))
        );

        _mockCache.Verify(c => c.SetAsync(
            It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()
        ), Times.Never);
    }

    [Fact]
    public async Task AddItemAsync_WhenCatalogCannotBeReached_RefusesRatherThanPricingItAtZero() {
        string customerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
        _mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync((byte[]?)null);
        _catalog.Setup(c => c.GetProductAsync("TSHIRT-BLK-M", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CatalogUnavailableException("TSHIRT-BLK-M", new Exception("no route")));

        await Assert.ThrowsAsync<CatalogUnavailableException>(() =>
            _cartService.AddItemAsync(customerPrincipalId, new AddCartItemRequest("TSHIRT-BLK-M", 1))
        );
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
