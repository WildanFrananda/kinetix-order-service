using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Kinetix.OrderService.Tests;

public class OrderApiAuthTests {
    private const string Admin = "0b7f3c52-6a1e-4f9d-8c2b-5e4a1d7f9c30";
    private const string Owner = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly Guid OrderId = Guid.Parse("5c0f2a8e-9d41-4b7a-8e3c-1f6d2b9a7e05");

    private static OrderResponse AnOrderOf(string customerPrincipalId) => new(
        OrderId,
        "ORD-20261021-0001",
        customerPrincipalId,
        Merchant,
        null,
        "PAID",
        150000m,
        0m,
        null,
        165000m,
        "Jl. Sudirman No. 45, Jakarta",
        "KINETIX_REGULAR",
        15000m,
        0m,
        15000m,
        4.2,
        "DISTANCE_QUOTED",
        DateTime.UtcNow,
        []
    );

    private static void InMemoryOrders(IServiceCollection services) {
        var name = Guid.NewGuid().ToString();
        services.AddDbContext<OrderDbContext>(options => options.UseInMemoryDatabase(name));
    }

    private static HttpRequestMessage Signed(HttpMethod method, string url, string token) {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Theory]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("customer", HttpStatusCode.Forbidden)]
    [InlineData("seller", HttpStatusCode.Forbidden)]
    public async Task OnlyAnAdminTokenFromIdentityOpensTheSagaQueue(string role, HttpStatusCode expected) {
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, InMemoryOrders);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/sagas/needs-attention/count",
            identity.AccessToken(Admin, role)
        ));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task AnAdminTokenFromIdentityReadsAnotherBuyersOrder() {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.GetOrderByIdAsync(OrderId)).ReturnsAsync(AnOrderOf(Owner));

        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/{OrderId}",
            identity.AccessToken(Admin, "admin")
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ABuyerTokenFromIdentityReadsTheirOwnOrderBySubject() {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.GetOrderByIdAsync(OrderId)).ReturnsAsync(AnOrderOf(Owner));

        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var mine = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/{OrderId}",
            identity.AccessToken(Owner, "customer")
        ));
        using var theirs = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/{OrderId}",
            identity.AccessToken(Admin, "customer")
        ));

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
    }

    [Fact]
    public async Task AnAdminCompletingAnOrderIsRecordedUnderTheSubjectOfTheirToken() {
        var completion = new Mock<IOrderCompletion>();
        completion.Setup(c => c.CompleteAsync(
            "ORD-20261021-0001",
            false,
            It.IsAny<LifecycleActor>(),
            It.IsAny<CancellationToken>()
        )).ReturnsAsync(new CompletionOutcome(CompletionStatus.Completed, true, null));

        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(completion.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/ORD-20261021-0001/complete",
            identity.AccessToken(Admin, "admin")
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        completion.Verify(
            c => c.CompleteAsync(
                "ORD-20261021-0001",
                false,
                LifecycleActor.Admin(Admin),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task AJwksIdentityCannotServeIsAnOutageNotAnInvalidToken() {
        await using var publishing = await TestIdentityIssuer.PublishingAsync();
        var unreachable = TestIdentityIssuer.Unreachable();
        await using var host = await OrderApiHost.StartAsync(unreachable.JwksUrl, InMemoryOrders);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/sagas/needs-attention/count",
            publishing.AccessToken(Admin, "admin")
        ));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AKeyIdentityDoesNotPublishIsStillAnInvalidToken() {
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, InMemoryOrders);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/sagas/needs-attention/count",
            TestIdentityIssuer.ForgedAccessToken(Admin, "admin")
        ));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer error=\"invalid_token\"", response.Headers.NonValidated["WWW-Authenticate"].ToString());
    }

    [Fact]
    public async Task TheCreatedLocationDoesNotNameTheHostTheGatewayCalled() {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.CheckoutAsync(Owner, It.IsAny<CheckoutRequest>(), It.IsAny<string?>()))
            .ReturnsAsync(AnOrderOf(Owner));

        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var request = Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/checkout",
            identity.AccessToken(Owner, "customer")
        );
        request.Headers.Host = "kinetix-order-service:8001";
        request.Content = JsonContent.Create(new {
            shippingAddress = "Jl. Sudirman No. 45, Jakarta",
            recipientName = "Test Buyer",
            recipientPhone = "081200000000",
        });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.False(response.Headers.Location!.IsAbsoluteUri, $"Location was {response.Headers.Location}");
        Assert.Equal($"/api/v1/orders/{OrderId}", response.Headers.Location.OriginalString);
    }
}
