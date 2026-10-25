using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Products;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Kinetix.OrderService.Tests;

public class OrderApiRequestTests {
    private const string Admin = "0b7f3c52-6a1e-4f9d-8c2b-5e4a1d7f9c30";
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";

    private static HttpRequestMessage Signed(HttpMethod method, string url, string token, object? body = null) {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    [Fact]
    public async Task ACheckoutWithNoShippingAddressSaysWhichFieldIsMissing() {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.CheckoutAsync(Buyer, It.IsAny<CheckoutRequest>(), It.IsAny<string?>()))
            .Returns((string _, CheckoutRequest request, string? _) => {
                CheckoutRecipient.From(request);
                return Task.FromException<OrderResponse>(
                    new InvalidOperationException("a checkout with no address reached the saga")
                );
            });
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/checkout",
            identity.AccessToken(Buyer, "customer"),
            new { recipientName = "Test Buyer", recipientPhone = "081200000000" }
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("RECIPIENT_REQUIRED", body);
        Assert.Contains("shippingAddress", body);
    }

    [Fact]
    public async Task ARejectionWithNoReasonSaysAReasonIsRequired() {
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            var name = Guid.NewGuid().ToString();
            services.AddDbContext<OrderDbContext>(options => options.UseInMemoryDatabase(name));
            services.AddSingleton<IOrderRowLock>(new UnlockedOrderRows());
            services.AddScoped<IReturnRejection, ReturnRejection>();
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/returns/RET-20261021-0001/reject",
            identity.AccessToken(Admin, "admin"),
            new { }
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("REASON_REQUIRED", body);
    }

    [Fact]
    public async Task ACartVoucherCanBeTakenOff() {
        var carts = new Mock<ICartService>();
        carts.Setup(c => c.RemoveVoucherAsync(Buyer)).ReturnsAsync(new CustomerCart(Buyer));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(carts.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Delete,
            $"{host.Address}/api/v1/cart/voucher",
            identity.AccessToken(Buyer, "customer")
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        carts.Verify(c => c.RemoveVoucherAsync(Buyer), Times.Once);
    }

    [Fact]
    public async Task ABlankVoucherCodeIsRefusedWithAPointerToTakingItOff() {
        var carts = new Mock<ICartService>();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(carts.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/cart/voucher",
            identity.AccessToken(Buyer, "customer"),
            new { voucherCode = "" }
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("VOUCHER_CODE_REQUIRED", body);
        carts.Verify(c => c.ApplyVoucherAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnOmittedQuantityIsRefusedRatherThanAddedAsZero() {
        var catalog = new Mock<IProductDirectory>();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ICartStore>(new InMemoryCartStore());
            services.AddSingleton(catalog.Object);
            services.AddScoped<ICartService, CartService>();
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/cart/items",
            identity.AccessToken(Buyer, "customer"),
            new { productId = "TSHIRT-BLK-M" }
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("QUANTITY_OUT_OF_RANGE", body);
    }

    [Fact]
    public async Task ASellerAddingItsOwnProductIsRefusedWithACode() {
        var catalog = new Mock<IProductDirectory>();
        catalog.Setup(c => c.GetProductAsync("TSHIRT-BLK-M", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogProduct("TSHIRT-BLK-M", Buyer, "Shirt", 150000m, null, 250));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ICartStore>(new InMemoryCartStore());
            services.AddSingleton(catalog.Object);
            services.AddScoped<ICartService, CartService>();
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/cart/items",
            identity.AccessToken(Buyer, "seller"),
            new { productId = "TSHIRT-BLK-M", quantity = 1 }
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("OWN_PRODUCT_NOT_PURCHASABLE", body);
    }

    [Fact]
    public async Task TheOrderListSaysHowManyOrdersThereAreInAll() {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.GetCustomerOrdersAsync(Buyer, OrderStatus.PAID, 2, 5))
            .ReturnsAsync(new CustomerOrderPage([], 12));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/my-orders?status=paid&page=2&pageSize=5",
            identity.AccessToken(Buyer, "customer")
        ));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(12, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(5, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AnEmptyProductIdIsARequestFaultNotAProductCatalogLacks(string? productId) {
        var catalog = new Mock<IProductDirectory>(MockBehavior.Strict);
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ICartStore>(new InMemoryCartStore());
            services.AddSingleton(catalog.Object);
            services.AddScoped<ICartService, CartService>();
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/cart/items",
            identity.AccessToken(Buyer, "customer"),
            new { productId, quantity = 1 }
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("PRODUCT_NOT_IN_CATALOG", body);
    }

    [Fact]
    public async Task ABodyTheJsonReaderCannotReadSaysSoWithoutTheReadersOwnText() {
        var carts = new Mock<ICartService>(MockBehavior.Strict);
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(carts.Object);
        });
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host.Address}/api/v1/cart/items") {
            Content = new StringContent(
                "{\"productId\":\"TSHIRT-BLK-M\",\"quantity\":\"two\"}",
                System.Text.Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            identity.AccessToken(Buyer, "customer")
        );

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("System.", body);
        Assert.DoesNotContain("Kinetix.OrderService", body);
        Assert.DoesNotContain("BytePositionInLine", body);
    }

    [Theory]
    [InlineData("CART_EMPTY")]
    [InlineData("CART_SPANS_TWO_MERCHANTS")]
    public async Task ACartThatCannotBeCheckedOutAsItStandsIsUnprocessableNotMalformed(string code) {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.CheckoutAsync(Buyer, It.IsAny<CheckoutRequest>(), It.IsAny<string?>()))
            .ThrowsAsync(code == "CART_EMPTY" ? new EmptyCartException() : new CartSpansTwoMerchantsException(2));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/checkout",
            identity.AccessToken(Buyer, "customer"),
            new {
                shippingAddress = "Jl. Sudirman No. 45, Jakarta",
                recipientName = "Test Buyer",
                recipientPhone = "081200000000",
            }
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(code, body);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=-5")]
    [InlineData("pageSize=101")]
    [InlineData("page=10001")]
    [InlineData("page=21474838&pageSize=100")]
    [InlineData("page=2147483647")]
    [InlineData("status=1")]
    [InlineData("status=PAID,CANCELLED")]
    [InlineData("status=LOST")]
    public async Task AnOrderListQueryOutsideWhatItCanAnswerIsRefused(string query) {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.GetCustomerOrdersAsync(
            It.IsAny<string>(),
            It.IsAny<OrderStatus?>(),
            It.IsAny<int>(),
            It.IsAny<int>()
        )).ReturnsAsync(new CustomerOrderPage([], 0));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/my-orders?{query}",
            identity.AccessToken(Buyer, "customer")
        ));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
