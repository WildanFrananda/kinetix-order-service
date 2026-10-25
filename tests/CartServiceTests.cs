using Moq;
using Xunit;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Products;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.DTOs.Requests;

namespace Kinetix.OrderService.Tests;

public class CartServiceTests {
    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";

    private readonly InMemoryCartStore _store = new();
    private readonly Mock<IProductDirectory> _catalog = new();
    private readonly CartService _cartService;

    private static readonly CatalogProduct Shirt = new(
        ProductId: "TSHIRT-BLK-M",
        MerchantPrincipalId: "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
        Title: "Kinetix Premium Shirt",
        UnitPrice: 150000m,
        CategoryId: "apparel",
        WeightGrams: 250
    );

    public CartServiceTests() {
        _catalog.Setup(c => c.GetProductAsync(Shirt.ProductId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Shirt);
        _cartService = new CartService(_store, _catalog.Object);
    }

    private void Holding(int quantity, string? voucher = null) {
        var cart = new CustomerCart(Customer) { AppliedVoucherCode = voucher };
        cart.Items.Add(new CartItem {
            ProductId = Shirt.ProductId,
            ProductTitle = Shirt.Title,
            UnitPrice = Shirt.UnitPrice,
            Quantity = quantity,
            MerchantPrincipalId = Shirt.MerchantPrincipalId,
        });
        _store.Put(cart);
    }

    [Fact]
    public async Task AddItemAsync_AddsNewItemToCart() {
        var request = new AddCartItemRequest("TSHIRT-BLK-M", 2);

        var result = await _cartService.AddItemAsync(Customer, request);

        Assert.Single(result.Items);
        Assert.Equal("TSHIRT-BLK-M", result.Items[0].ProductId);
        Assert.Equal(2, result.Items[0].Quantity);
        Assert.Equal(300000m, result.Subtotal);

        Assert.Equal(150000m, result.Items[0].UnitPrice);
        Assert.Equal("Kinetix Premium Shirt", result.Items[0].ProductTitle);
        Assert.Equal("apparel", result.Items[0].CategoryId);
        Assert.Equal("3aa957c8-b802-4d58-b9fc-f7b76ce60fa3", result.Items[0].MerchantPrincipalId);
        Assert.Equal(2, (await _cartService.GetCartAsync(Customer)).Items[0].Quantity);
    }

    [Fact]
    public async Task AddItemAsync_WhenCatalogHasNoSuchProduct_RefusesRatherThanCarryingTheBuyersWord() {
        _catalog.Setup(c => c.GetProductAsync("GHOST-01", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProductNotInCatalogException("GHOST-01"));

        await Assert.ThrowsAsync<ProductNotInCatalogException>(() =>
            _cartService.AddItemAsync(Customer, new AddCartItemRequest("GHOST-01", 1))
        );

        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task AddItemAsync_WhenCatalogCannotBeReached_RefusesRatherThanPricingItAtZero() {
        _catalog.Setup(c => c.GetProductAsync("TSHIRT-BLK-M", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CatalogUnavailableException("TSHIRT-BLK-M", new Exception("no route")));

        await Assert.ThrowsAsync<CatalogUnavailableException>(() =>
            _cartService.AddItemAsync(Customer, new AddCartItemRequest("TSHIRT-BLK-M", 1))
        );
    }

    [Fact]
    public async Task RemoveItemAsync_RemovesItemFromCart() {
        Holding(1);

        var result = await _cartService.RemoveItemAsync(Customer, Shirt.ProductId);

        Assert.Empty(result.Items);
        Assert.Equal(0m, result.Subtotal);
    }

    [Fact]
    public async Task AddItemAsync_RefusesTheBuyersOwnProduct() {
        var refusal = await Assert.ThrowsAsync<OwnProductNotPurchasableException>(() =>
            _cartService.AddItemAsync(Shirt.MerchantPrincipalId, new AddCartItemRequest(Shirt.ProductId, 1))
        );

        Assert.Equal(Shirt.ProductId, refusal.ProductId);
        Assert.Equal(0, _store.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(1000)]
    public async Task AddItemAsync_RefusesAQuantityOutsideOneTo999(int quantity) {
        var refusal = await Assert.ThrowsAsync<QuantityOutOfRangeException>(() =>
            _cartService.AddItemAsync(Customer, new AddCartItemRequest(Shirt.ProductId, quantity))
        );

        Assert.Equal(1, refusal.Minimum);
        Assert.Equal(CartItem.MaxQuantity, refusal.Maximum);
        Assert.Equal(0, _store.Writes);
        _catalog.Verify(c => c.GetProductAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddItemAsync_RefusesToTakeALinePastTheMostACartHolds() {
        Holding(990);

        var refusal = await Assert.ThrowsAsync<CartLineQuantityOutOfRangeException>(() =>
            _cartService.AddItemAsync(Customer, new AddCartItemRequest(Shirt.ProductId, 10))
        );

        Assert.Equal(1000, refusal.Quantity);
        Assert.Equal(990, (await _cartService.GetCartAsync(Customer)).Items[0].Quantity);
    }

    [Fact]
    public async Task AddItemAsync_AddsOnTopOfWhatIsThere() {
        Holding(3);

        var result = await _cartService.AddItemAsync(Customer, new AddCartItemRequest(Shirt.ProductId, 2));

        Assert.Equal(5, Assert.Single(result.Items).Quantity);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public async Task UpdateItemQuantityAsync_RefusesAQuantityOutsideZeroTo999(int quantity) {
        Holding(3);

        await Assert.ThrowsAsync<QuantityOutOfRangeException>(() =>
            _cartService.UpdateItemQuantityAsync(Customer, Shirt.ProductId, quantity)
        );

        Assert.Equal(3, (await _cartService.GetCartAsync(Customer)).Items[0].Quantity);
    }

    [Fact]
    public async Task UpdateItemQuantityAsync_ZeroTakesTheLineOut() {
        Holding(3);

        var result = await _cartService.UpdateItemQuantityAsync(Customer, Shirt.ProductId, 0);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task RemoveVoucherAsync_TakesTheVoucherOffAndKeepsTheItems() {
        Holding(2, "DISCOUNT10");

        var result = await _cartService.RemoveVoucherAsync(Customer);

        Assert.Null(result.AppliedVoucherCode);
        Assert.Null((await _cartService.GetCartAsync(Customer)).AppliedVoucherCode);
        Assert.Equal(2, Assert.Single(result.Items).Quantity);
    }

    [Fact]
    public async Task ApplyVoucherAsync_RefusesACodeLongerThanAnOrderCanRecord() {
        var refusal = await Assert.ThrowsAsync<FieldTooLongException>(() =>
            _cartService.ApplyVoucherAsync(Customer, new string('V', 51))
        );

        Assert.Equal("voucherCode", refusal.Field);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task AnEditThatMeetsAConcurrentWriteIsAppliedAgainOnTheNewerCart() {
        Holding(3);
        _store.ConflictsToInject = 2;

        var result = await _cartService.AddItemAsync(Customer, new AddCartItemRequest(Shirt.ProductId, 1));

        Assert.Equal(4, Assert.Single(result.Items).Quantity);
        Assert.Equal(4, (await _cartService.GetCartAsync(Customer)).Items[0].Quantity);
    }

    [Fact]
    public async Task AnEditThatKeepsMeetingConcurrentWritesSaysSoRatherThanDroppingIt() {
        Holding(3);
        _store.ConflictsToInject = CartService.MaxEditAttempts;

        await Assert.ThrowsAsync<CartBusyException>(() =>
            _cartService.AddItemAsync(Customer, new AddCartItemRequest(Shirt.ProductId, 1))
        );

        Assert.Equal(3, (await _cartService.GetCartAsync(Customer)).Items[0].Quantity);
    }

    [Fact]
    public async Task ACheckoutTakesOutOnlyWhatItBought_KeepingWhatWasAddedMeanwhile() {
        Holding(2, voucher: "SAVE10");
        var checkedOut = await _cartService.GetCartAsync(Customer);
        Holding(5, voucher: "SAVE10");

        await _cartService.RemoveCheckedOutAsync(Customer, checkedOut);

        var cart = await _cartService.GetCartAsync(Customer);
        Assert.Equal(3, Assert.Single(cart.Items).Quantity);
        Assert.Null(cart.AppliedVoucherCode);
    }

    [Fact]
    public async Task AVoucherChangedWhileTheCheckoutRanStaysOnTheCart() {
        Holding(2, voucher: "SAVE10");
        var checkedOut = await _cartService.GetCartAsync(Customer);
        Holding(2, voucher: "FREESHIP");

        await _cartService.RemoveCheckedOutAsync(Customer, checkedOut);

        var cart = await _cartService.GetCartAsync(Customer);
        Assert.Empty(cart.Items);
        Assert.Equal("FREESHIP", cart.AppliedVoucherCode);
    }

    [Fact]
    public async Task ALineLoweredBelowWhatWasBoughtLeavesTheCart() {
        Holding(3);
        var checkedOut = await _cartService.GetCartAsync(Customer);
        Holding(1);

        await _cartService.RemoveCheckedOutAsync(Customer, checkedOut);

        Assert.Empty((await _cartService.GetCartAsync(Customer)).Items);
    }

    [Fact]
    public async Task TakingOutWhatWasBoughtMeetsAConcurrentWriteAndIsAppliedAgain() {
        Holding(2);
        var checkedOut = await _cartService.GetCartAsync(Customer);
        _store.ConflictsToInject = 2;

        await _cartService.RemoveCheckedOutAsync(Customer, checkedOut);

        Assert.Empty((await _cartService.GetCartAsync(Customer)).Items);
    }

    [Fact]
    public async Task TakingOutWhatWasBoughtThatKeepsMeetingWritesSaysSo() {
        Holding(2);
        var checkedOut = await _cartService.GetCartAsync(Customer);
        _store.ConflictsToInject = CartService.MaxEditAttempts;

        await Assert.ThrowsAsync<CartBusyException>(() => _cartService.RemoveCheckedOutAsync(Customer, checkedOut));

        Assert.Equal(2, Assert.Single((await _cartService.GetCartAsync(Customer)).Items).Quantity);
    }
}
