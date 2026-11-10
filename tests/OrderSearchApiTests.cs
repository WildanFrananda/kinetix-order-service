using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Observability;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Kinetix.OrderService.Tests;

public class OrderSearchApiTests {
    private const string Admin = "0b7f3c52-6a1e-4f9d-8c2b-5e4a1d7f9c30";
    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";
    private const string OrderNumber = "ORD-20261110-0001";

    private const string NoDatabase =
        "Host=127.0.0.1;Port=1;Database=kinetix_order_none;Username=nobody;Password=none;Timeout=2";

    private static readonly DateTime PlacedAt = new(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc);

    private static readonly OrderSummaryResponse Summary = new(
        Guid.Parse("5c0f2a8e-9d41-4b7a-8e3c-1f6d2b9a7e05"),
        OrderNumber,
        "DELIVERED",
        Customer,
        Merchant,
        165000m,
        15000m,
        3,
        PlacedAt,
        PlacedAt.AddHours(5),
        PlacedAt.AddHours(4),
        null
    );

    private static HttpRequestMessage Signed(string url, string token, string requestId = "req-search") {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Request-Id", requestId);

        return request;
    }

    private static Mock<IOrderSearch> Answering(OrderSearchPage page) {
        var search = new Mock<IOrderSearch>(MockBehavior.Strict);
        search
            .Setup(s => s.SearchAsync(It.IsAny<OrderSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        return search;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> AdminActionsIn(CapturedLogs logs) =>
        [.. logs.Entries.Where(e => Equals(e.GetValueOrDefault("event"), AdminAudit.EventName))];

    private static Task<OrderApiHost> HostAsync(
        TestIdentityIssuer identity,
        Mock<IOrderSearch> search,
        CapturedLogs? logs = null
    ) =>
        OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton(search.Object);
            if (logs is not null) {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        });

    [Theory]
    [InlineData("customer")]
    [InlineData("seller")]
    [InlineData("courier")]
    public async Task OnlyAnAdminSearchesOrders(string role) {
        var search = new Mock<IOrderSearch>(MockBehavior.Strict);
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await HostAsync(identity, search, logs);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search?merchant={Merchant}",
            identity.AccessToken(Merchant, role)
        ));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        search.VerifyNoOtherCalls();
        Assert.Empty(AdminActionsIn(logs));
    }

    [Fact]
    public async Task ASearchWithoutATokenIsUnauthorized() {
        var search = new Mock<IOrderSearch>(MockBehavior.Strict);
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await HostAsync(identity, search);
        using var client = new HttpClient();

        using var response = await client.GetAsync($"{host.Address}/api/v1/orders/search");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnAdminGetsEachOrdersSummaryAndNothingThatLocatesTheBuyer() {
        var next = new OrderSearchCursor(Summary.CreatedAt, Summary.Id);
        var search = Answering(new OrderSearchPage([Summary], next));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await HostAsync(identity, search);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search",
            identity.AccessToken(Admin, "admin")
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(["orders", "nextCursor"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(next.Encode(), root.GetProperty("nextCursor").GetString());

        var order = Assert.Single(root.GetProperty("orders").EnumerateArray());
        Assert.Equal(
            [
                "id",
                "orderNumber",
                "status",
                "customerPrincipalId",
                "merchantPrincipalId",
                "finalTotal",
                "finalShippingFee",
                "itemCount",
                "createdAt",
                "updatedAt",
                "deliveredAt",
                "completedAt",
            ],
            order.EnumerateObject().Select(p => p.Name)
        );
        Assert.Equal(3, order.GetProperty("itemCount").GetInt32());
        Assert.Equal(165000m, order.GetProperty("finalTotal").GetDecimal());
        Assert.Equal("2026-11-10T03:00:00Z", order.GetProperty("createdAt").GetString());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("completedAt").ValueKind);

        foreach (var located in new[] { "address", "recipient", "phone", "items", "latitude" }) {
            Assert.DoesNotContain(located, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheLastPageAnswersANullCursor() {
        var search = Answering(new OrderSearchPage([], null));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await HostAsync(identity, search);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search",
            identity.AccessToken(Admin, "admin")
        ));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(json.RootElement.GetProperty("orders").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task TheFiltersReachTheSearchAsParsed() {
        var search = Answering(new OrderSearchPage([], null));
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await HostAsync(identity, search);
        using var client = new HttpClient();
        var before = new OrderSearchCursor(PlacedAt, Summary.Id);

        using var response = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/SEARCH?status=PAID,DELIVERED&createdFrom=2026-11-01T00:00:00%2B07:00"
                + $"&createdTo=2026-11-10T00:00:00Z&merchant={Merchant}&customer={Customer}"
                + $"&orderNumber={OrderNumber}&before={before.Encode()}&limit=20",
            identity.AccessToken(Admin, "admin")
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        search.Verify(
            s => s.SearchAsync(
                It.Is<OrderSearchQuery>(q =>
                    q.Statuses.SetEquals(new[] { OrderStatus.PAID, OrderStatus.DELIVERED })
                    && q.CreatedFrom == new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc)
                    && q.CreatedTo == new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc)
                    && q.MerchantPrincipalId == Merchant
                    && q.CustomerPrincipalId == Customer
                    && q.OrderNumber == OrderNumber
                    && q.Before == before
                    && q.Limit == 20
                ),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Theory]
    [InlineData("status=PAID,LOST", "INVALID_STATUS", "status")]
    [InlineData("createdFrom=2026-11-10T00:00:00", "INVALID_CREATED_RANGE", "createdFrom")]
    [InlineData(
        "createdFrom=2026-11-10T00:00:00Z&createdTo=2026-11-01T00:00:00Z",
        "INVALID_CREATED_RANGE",
        "createdTo"
    )]
    [InlineData("merchant=not-a-principal", "INVALID_MERCHANT", "merchant")]
    [InlineData("customer=not-a-principal", "INVALID_CUSTOMER", "customer")]
    [InlineData("orderNumber=", "INVALID_ORDER_NUMBER", "orderNumber")]
    [InlineData("before=bm90LWEtY3Vyc29y", "INVALID_CURSOR", "before")]
    [InlineData("limit=101", "INVALID_LIMIT", "limit")]
    [InlineData("page=2", "UNKNOWN_PARAMETER", "page")]
    public async Task AMalformedParameterIsABadRequestNamingItNeverAnEmptyPage(
        string queryString,
        string code,
        string parameter
    ) {
        var search = new Mock<IOrderSearch>(MockBehavior.Strict);
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await HostAsync(identity, search, logs);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search?{queryString}",
            identity.AccessToken(Admin, "admin")
        ));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(parameter, json.RootElement.GetProperty("parameter").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
        Assert.False(json.RootElement.TryGetProperty("orders", out _));
        search.VerifyNoOtherCalls();

        var entry = Assert.Single(AdminActionsIn(logs));
        Assert.Equal(AdminActions.OrderSearch, entry["action"]);
        Assert.Equal(AdminActionOutcome.Refused, entry["outcome"]);
        Assert.Contains(
            "kinetix_admin_actions_total{action=\"order.search\",outcome=\"refused\"} 1",
            await host.ScrapeAsync()
        );
    }

    [Fact]
    public async Task ASearchIsAuditedByTheNamesOfItsFiltersNeverTheirValues() {
        var search = Answering(new OrderSearchPage([Summary], null));
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await HostAsync(identity, search, logs);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search?orderNumber={OrderNumber}&customer={Customer}&limit=5"
                + $"&merchant={Merchant}&status=DELIVERED&createdFrom=2026-11-01T00:00:00Z",
            identity.AccessToken(Admin, "admin"),
            "req-search-audit"
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(AdminActionsIn(logs));
        Assert.Equal("Information", entry["level"]);
        Assert.Equal(Admin, entry["actor"]);
        Assert.Equal(AdminActions.OrderSearch, entry["action"]);
        Assert.Equal("orders/search?status&createdFrom&merchant&customer&orderNumber&limit", entry["target"]);
        Assert.Equal(AdminActionOutcome.Done, entry["outcome"]);
        Assert.Equal("req-search-audit", entry["request_id"]);

        var logged = string.Join("\n", entry.Values.Select(v => v?.ToString()));
        foreach (var value in new[] { Customer, Merchant, OrderNumber, "DELIVERED", "2026-11-01" }) {
            Assert.DoesNotContain(value, logged, StringComparison.Ordinal);
        }

        var scrape = await host.ScrapeAsync();
        Assert.Contains("kinetix_admin_actions_total{action=\"order.search\",outcome=\"done\"} 1", scrape);
        Assert.Contains("kinetix_admin_actions_total{action=\"order.search\",outcome=\"failed\"} 0", scrape);
    }

    [Fact]
    public async Task ASearchWithNoFiltersIsAuditedAgainstTheSearchItself() {
        var search = Answering(new OrderSearchPage([], null));
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await HostAsync(identity, search, logs);
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search",
            identity.AccessToken(Admin, "admin")
        ));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("orders/search", Assert.Single(AdminActionsIn(logs))["target"]);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeReachedIsUnavailableAndTheSearchIsAuditedAsFailed() {
        var logs = new CapturedLogs();
        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddDbContext<OrderDbContext>(options => options.UseNpgsql(NoDatabase));
            services.AddScoped<IOrderSearchRepository, OrderSearchRepository>();
            services.AddScoped<IOrderSearch, OrderSearch>();
        });
        using var client = new HttpClient();

        using var response = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search?status=PAID",
            identity.AccessToken(Admin, "admin")
        ));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("DATABASE_UNAVAILABLE", body);
        Assert.Equal(AdminActionOutcome.Failed, Assert.Single(AdminActionsIn(logs))["outcome"]);
    }
}
