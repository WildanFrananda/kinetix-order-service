using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Kinetix.OrderService.Tests;

public class OrderApiFaultTests {
    private const string Admin = "0b7f3c52-6a1e-4f9d-8c2b-5e4a1d7f9c30";

    private const string NoDatabase =
        "Host=127.0.0.1;Port=1;Database=kinetix_order_none;Username=nobody;Password=none;Timeout=2";

    private static HttpRequestMessage Signed(HttpMethod method, string url, string token, string? json = null) {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Fact]
    public async Task ABodyLargerThanTheServerAcceptsIsRefusedAsTooLargeRatherThanAFault() {
        var rejection = new Mock<IReturnRejection>(MockBehavior.Strict);
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(
            identity.JwksUrl,
            services => {
                var name = Guid.NewGuid().ToString();
                services.AddDbContext<OrderDbContext>(options => options.UseInMemoryDatabase(name));
                services.AddSingleton(rejection.Object);
            },
            kestrel => kestrel.Limits.MaxRequestBodySize = 1024
        );
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/returns/RET-20261022-0001/reject",
            identity.AccessToken(Admin, "admin"),
            JsonSerializer.Serialize(new { reason = new string('x', 4096) })
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("REQUEST_BODY_TOO_LARGE", body);
        Assert.DoesNotContain("1024", body);
    }

    [Theory]
    [InlineData("GET", "/api/v1/orders/sagas/needs-attention/count")]
    [InlineData("GET", "/api/v1/orders/returns/needs-attention")]
    [InlineData("POST", "/api/v1/orders/returns/RET-20261022-0002/reject")]
    public async Task ADatabaseThatCannotBeReachedIsUnavailableRatherThanAFault(string method, string path) {
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddDbContext<OrderDbContext>(options => options.UseNpgsql(NoDatabase));
            services.AddSingleton<IOrderRowLock>(new UnlockedOrderRows());
            services.AddScoped<IReturnRejection, ReturnRejection>();
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            new HttpMethod(method),
            $"{host.Address}{path}",
            identity.AccessToken(Admin, "admin"),
            method == "POST" ? JsonSerializer.Serialize(new { reason = "the goods never came back" }) : null
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("DATABASE_UNAVAILABLE", body);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("kinetix_order_none", body);
    }

    [Fact]
    public async Task AFaultThatIsNotAnOutageIsStillAnInternalError() {
        var rejection = new Mock<IReturnRejection>();
        rejection
            .Setup(r => r.RejectAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<LifecycleActor>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new InvalidOperationException("a defect at kinetix-payment-service:50056"));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            var name = Guid.NewGuid().ToString();
            services.AddDbContext<OrderDbContext>(options => options.UseInMemoryDatabase(name));
            services.AddSingleton(rejection.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/returns/RET-20261022-0003/reject",
            identity.AccessToken(Admin, "admin"),
            JsonSerializer.Serialize(new { reason = "the goods never came back" })
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("INTERNAL_ERROR", body);
        Assert.DoesNotContain("kinetix-payment-service", body);
    }
}
