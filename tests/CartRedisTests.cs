using System.Text.Json;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Products;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.Infrastructure.Redis;
using Moq;
using StackExchange.Redis;

namespace Kinetix.OrderService.Tests;

public class CartRedisTests {
    private static readonly CatalogProduct Shirt = new(
        ProductId: "TSHIRT-BLK-M",
        MerchantPrincipalId: "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
        Title: "Kinetix Premium Shirt",
        UnitPrice: 150000m,
        CategoryId: "apparel",
        WeightGrams: 250
    );

    public static string? RedisUrl => Environment.GetEnvironmentVariable("KINETIX_TEST_REDIS");

    private static CartService CartsOver(IConnectionMultiplexer redis) {
        var catalog = new Mock<IProductDirectory>();
        catalog.Setup(c => c.GetProductAsync(Shirt.ProductId, It.IsAny<CancellationToken>())).ReturnsAsync(Shirt);
        return new CartService(new RedisCartStore(redis), catalog.Object);
    }

    [Fact]
    public async Task EveryOneOfManyConcurrentAddsToOneCartLands() {
        if (RedisUrl is null) {
            return;
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisUrl);
        var carts = CartsOver(redis);
        var buyer = Guid.NewGuid().ToString();

        await Task.WhenAll(Enumerable.Range(0, CartService.MaxEditAttempts).Select(_ =>
            Task.Run(() => carts.AddItemAsync(buyer, new AddCartItemRequest(Shirt.ProductId, 1)))
        ));

        var cart = await carts.GetCartAsync(buyer);
        Assert.Equal(CartService.MaxEditAttempts, Assert.Single(cart.Items).Quantity);
    }

    [Fact]
    public async Task AWriteAgainstAVersionSomeoneElseReplacedIsRefused() {
        if (RedisUrl is null) {
            return;
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisUrl);
        var store = new RedisCartStore(redis);
        var buyer = Guid.NewGuid().ToString();

        var first = await store.ReadAsync(buyer);
        var second = await store.ReadAsync(buyer);

        Assert.True(await store.TryReplaceAsync(buyer, first.Cart, first.Version, TimeSpan.FromMinutes(1)));
        Assert.False(await store.TryReplaceAsync(buyer, second.Cart, second.Version, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ADeleteAgainstAVersionSomeoneElseReplacedIsRefused() {
        if (RedisUrl is null) {
            return;
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisUrl);
        var store = new RedisCartStore(redis);
        var carts = CartsOver(redis);
        var buyer = Guid.NewGuid().ToString();
        await carts.AddItemAsync(buyer, new AddCartItemRequest(Shirt.ProductId, 1));

        var read = await store.ReadAsync(buyer);
        await carts.AddItemAsync(buyer, new AddCartItemRequest(Shirt.ProductId, 1));

        Assert.False(await store.TryDeleteAsync(buyer, read.Version));
        Assert.Equal(2, Assert.Single((await carts.GetCartAsync(buyer)).Items).Quantity);

        var current = await store.ReadAsync(buyer);
        Assert.True(await store.TryDeleteAsync(buyer, current.Version));
        Assert.False(await redis.GetDatabase().KeyExistsAsync($"{RedisCartStore.KeyPrefix}{buyer}"));
    }

    [Fact]
    public async Task ALineAddedWhileACheckoutRanOutlivesTheCheckoutTakingItsLines() {
        if (RedisUrl is null) {
            return;
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisUrl);
        var carts = CartsOver(redis);
        var buyer = Guid.NewGuid().ToString();
        await carts.AddItemAsync(buyer, new AddCartItemRequest(Shirt.ProductId, 2));

        var checkedOut = await carts.GetCartAsync(buyer);
        await carts.AddItemAsync(buyer, new AddCartItemRequest(Shirt.ProductId, 1));
        await carts.RemoveCheckedOutAsync(buyer, checkedOut);

        Assert.Equal(1, Assert.Single((await carts.GetCartAsync(buyer)).Items).Quantity);

        await carts.RemoveCheckedOutAsync(buyer, await carts.GetCartAsync(buyer));
        Assert.False(await redis.GetDatabase().KeyExistsAsync($"{RedisCartStore.KeyPrefix}{buyer}"));
    }

    [Fact]
    public async Task ACartWrittenBeforeTheVersionedStoreIsReadAndEdited() {
        if (RedisUrl is null) {
            return;
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisUrl);
        var buyer = Guid.NewGuid().ToString();
        var legacy = new CustomerCart(buyer);
        legacy.Items.Add(new CartItem {
            ProductId = Shirt.ProductId,
            ProductTitle = Shirt.Title,
            UnitPrice = Shirt.UnitPrice,
            Quantity = 2,
            MerchantPrincipalId = Shirt.MerchantPrincipalId,
        });

        var key = $"{RedisCartStore.KeyPrefix}{buyer}";
        await redis.GetDatabase().HashSetAsync(key, [
            new HashEntry("absexp", DateTimeOffset.UtcNow.AddDays(14).Ticks),
            new HashEntry("sldexp", -1),
            new HashEntry("data", JsonSerializer.SerializeToUtf8Bytes(legacy)),
        ]);

        var carts = CartsOver(redis);
        var cart = await carts.AddItemAsync(buyer, new AddCartItemRequest(Shirt.ProductId, 1));

        Assert.Equal(3, Assert.Single(cart.Items).Quantity);
        Assert.Equal(3, Assert.Single((await carts.GetCartAsync(buyer)).Items).Quantity);
        Assert.True(await redis.GetDatabase().KeyTimeToLiveAsync(key) > TimeSpan.FromDays(13));
    }

    [Fact]
    public async Task ARedisThatCannotBeReachedIsReportedAsTheCartBeingUnavailable() {
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 200;
        options.SyncTimeout = 200;
        options.AsyncTimeout = 200;
        await using var redis = await ConnectionMultiplexer.ConnectAsync(options);

        await Assert.ThrowsAsync<CartUnavailableException>(() =>
            new RedisCartStore(redis).ReadAsync(Guid.NewGuid().ToString())
        );
    }
}
