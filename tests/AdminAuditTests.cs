using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Observability;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Kinetix.OrderService.Tests;

public class AdminAuditTests {
    private const string Admin = "0b7f3c52-6a1e-4f9d-8c2b-5e4a1d7f9c30";
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string OrderNumber = "ORD-20261022-0001";

    private static readonly Guid OrderId = Guid.Parse("5c0f2a8e-9d41-4b7a-8e3c-1f6d2b9a7e05");

    private static HttpRequestMessage Signed(
        HttpMethod method,
        string url,
        string token,
        string requestId,
        object? body = null
    ) {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Request-Id", requestId);
        if (body is not null) {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static OrderResponse AnOrderOf(string customerPrincipalId) => new(
        OrderId,
        OrderNumber,
        customerPrincipalId,
        "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
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
        "MATCHING_QUOTE",
        DateTime.UtcNow,
        []
    );

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> AdminActionsIn(CapturedLogs logs) =>
        [.. logs.Entries.Where(e => Equals(e.GetValueOrDefault("event"), AdminAudit.EventName))];

    private static string Counted(string action, string outcome, string value) =>
        $"kinetix_admin_actions_total{{action=\"{action}\",outcome=\"{outcome}\"}} {value}";

    [Fact]
    public async Task AnAdminCompletionIsLoggedWithWhoWhatAndTheRequest_AndCountedAsDone() {
        var completion = new Mock<IOrderCompletion>();
        completion
            .Setup(c => c.CompleteAsync(OrderNumber, false, LifecycleActor.Admin(Admin), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompletionOutcome(CompletionStatus.Completed, true, null));
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton(completion.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/{OrderNumber}/complete",
            identity.AccessToken(Admin, "admin"),
            "req-audit-complete"
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(AdminActionsIn(logs));
        Assert.Equal("Information", entry["level"]);
        Assert.Equal(Admin, entry["actor"]);
        Assert.Equal(AdminActions.Complete, entry["action"]);
        Assert.Equal(OrderNumber, entry["target"]);
        Assert.Equal(AdminActionOutcome.Done, entry["outcome"]);
        Assert.Equal("req-audit-complete", entry["request_id"]);

        var scrape = await host.ScrapeAsync();
        Assert.Contains(Counted(AdminActions.Complete, AdminActionOutcome.Done, "1"), scrape);
        Assert.Contains(Counted(AdminActions.Complete, AdminActionOutcome.Refused, "0"), scrape);
    }

    [Fact]
    public async Task ARejectionOfAReturnThatDoesNotExistIsCountedAsRefused() {
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            var name = Guid.NewGuid().ToString();
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddDbContext<OrderDbContext>(options => options.UseInMemoryDatabase(name));
            services.AddSingleton<IOrderRowLock>(new UnlockedOrderRows());
            services.AddScoped<IReturnRejection, ReturnRejection>();
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/returns/RET-NOBODY/reject",
            identity.AccessToken(Admin, "admin"),
            "req-audit-reject",
            new { reason = "the goods never came back" }
        ));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var entry = Assert.Single(AdminActionsIn(logs));
        Assert.Equal(AdminActions.ReturnReject, entry["action"]);
        Assert.Equal("RET-NOBODY", entry["target"]);
        Assert.Equal(AdminActionOutcome.Refused, entry["outcome"]);
        Assert.Contains(Counted(AdminActions.ReturnReject, AdminActionOutcome.Refused, "1"), await host.ScrapeAsync());
    }

    [Fact]
    public async Task AnAdminActionThatThrewIsCountedAsFailed() {
        var completion = new Mock<IOrderCompletion>();
        completion
            .Setup(c => c.CompleteAsync(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<LifecycleActor>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new InvalidOperationException("a defect"));
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton(completion.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/{OrderNumber}/complete",
            identity.AccessToken(Admin, "admin"),
            "req-audit-failed"
        ));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(AdminActionOutcome.Failed, Assert.Single(AdminActionsIn(logs))["outcome"]);
        Assert.Contains(Counted(AdminActions.Complete, AdminActionOutcome.Failed, "1"), await host.ScrapeAsync());
    }

    [Fact]
    public async Task ACallerWithoutTheAdminRoleIsRefusedBeforeAnyAdminActionIsRecorded() {
        var completion = new Mock<IOrderCompletion>(MockBehavior.Strict);
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton(completion.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Post,
            $"{host.Address}/api/v1/orders/{OrderNumber}/complete",
            identity.AccessToken(Buyer, "customer"),
            "req-audit-forbidden"
        ));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(AdminActionsIn(logs));
    }

    [Theory]
    [InlineData("GET", "/api/v1/orders/sagas/needs-attention", AdminActions.SagaAttentionList)]
    [InlineData("GET", "/api/v1/orders/sagas/needs-attention/count", AdminActions.SagaAttentionCount)]
    [InlineData("GET", "/api/v1/orders/returns/needs-attention", AdminActions.ReturnAttentionList)]
    public async Task EveryAdminQueueReadIsRecorded(string method, string path, string action) {
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            var name = Guid.NewGuid().ToString();
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddDbContext<OrderDbContext>(options => options.UseInMemoryDatabase(name));
            services.AddSingleton(Mock.Of<IReturnRejection>());
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            new HttpMethod(method),
            $"{host.Address}{path}",
            identity.AccessToken(Admin, "admin"),
            "req-audit-queue"
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(AdminActionsIn(logs));
        Assert.Equal(action, entry["action"]);
        Assert.Equal(AdminActionOutcome.Done, entry["outcome"]);
        Assert.Equal(Admin, entry["actor"]);
    }

    [Theory]
    [InlineData(Admin, "admin", Buyer, 1)]
    [InlineData(Admin, "admin", Admin, 0)]
    [InlineData(Buyer, "customer", Buyer, 0)]
    public async Task OnlyAnAdminReadingSomeoneElsesOrderIsAnAdminAction(
        string caller,
        string role,
        string owner,
        int recorded
    ) {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.GetOrderByIdAsync(OrderId)).ReturnsAsync(AnOrderOf(owner));
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/{OrderId}",
            identity.AccessToken(caller, role),
            "req-audit-read"
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = AdminActionsIn(logs);
        Assert.Equal(recorded, entries.Count);
        if (recorded > 0) {
            Assert.Equal(AdminActions.OrderRead, entries[0]["action"]);
            Assert.Equal(OrderId.ToString(), entries[0]["target"]);
            Assert.Equal(AdminActionOutcome.Done, entries[0]["outcome"]);
        }
    }

    [Fact]
    public async Task AnAdminReadOfAnOrderThatDoesNotExistIsRecordedAsRefused() {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.GetOrderByIdAsync(OrderId)).ReturnsAsync((OrderResponse?)null);
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton(orders.Object);
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            HttpMethod.Get,
            $"{host.Address}/api/v1/orders/{OrderId}",
            identity.AccessToken(Admin, "admin"),
            "req-audit-missing"
        ));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(AdminActionOutcome.Refused, Assert.Single(AdminActionsIn(logs))["outcome"]);
    }
}
