using System.Net;
using System.Text.RegularExpressions;
using Kinetix.OrderService.Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prometheus;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class MetricsEndpointTests {
    private static readonly Regex LabelValue = new("[a-z_]+=\"[^\"]*\"");

    private static readonly Regex LooksLikeAnIdentifier = new(
        "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}|[0-9a-f]{24,}|@[a-z0-9.-]+\\.[a-z]{2,}",
        RegexOptions.IgnoreCase
    );

    private static readonly Regex RouteCarryingAnId = new("route=\"[^\"]*/[0-9]+");

    [Fact]
    public async Task TheRouteLabelIsTheTemplateAndTheOrderIdReachesNoLabel() {
        var orderId = Guid.NewGuid();

        var body = await ScrapeAfter(async (client, baseAddress) => {
            using var response = await client.GetAsync($"{baseAddress}/api/v1/orders/{orderId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });

        Assert.Contains("route=\"/api/v1/orders/{orderId}\"", body);
        Assert.DoesNotContain(orderId.ToString(), body);
        AssertNoIdentifierReachedALabel(body);
    }

    [Fact]
    public async Task APathThatMatchedNothingDoesNotMintASeriesOfItsOwn() {
        var forged = Guid.NewGuid();

        var body = await ScrapeAfter(async (client, baseAddress) => {
            using var response = await client.GetAsync($"{baseAddress}/api/v1/orders/{forged}/forged");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        });

        Assert.Contains($"route=\"{RouteLabel.Unmatched}\"", body);
        Assert.DoesNotContain(forged.ToString(), body);
        AssertNoIdentifierReachedALabel(body);
    }

    [Fact]
    public async Task ItIsPrometheusTextAndCarriesEveryNameTheContractAsksThisServiceFor() {
        var body = await ScrapeAfter((_, _) => Task.CompletedTask);

        Assert.Contains("# HELP ", body);

        foreach (var metric in new[] {
            "kinetix_http_requests_total",
            "kinetix_http_request_duration_seconds",
            "kinetix_grpc_server_calls_total",
            "kinetix_grpc_client_calls_total",
            "kinetix_build_info",
        }) {
            Assert.Matches(new Regex($"^{metric}", RegexOptions.Multiline), body);
        }

        Assert.Matches(new Regex("^kinetix_http_request_duration_seconds_bucket", RegexOptions.Multiline), body);
        Assert.Matches(new Regex("^kinetix_http_request_duration_seconds_sum", RegexOptions.Multiline), body);
        Assert.Matches(new Regex("^kinetix_http_request_duration_seconds_count", RegexOptions.Multiline), body);
        Assert.Matches(new Regex("^kinetix_build_info\\{service=\"kinetix-order-service\",version=\"[^\"]+\"\\} 1", RegexOptions.Multiline), body);
    }

    [Fact]
    public async Task TheGrpcFamiliesAreReadableBeforeTheFirstCall() {
        var body = await ScrapeAfter((_, _) => Task.CompletedTask);

        Assert.Contains(
            "kinetix_grpc_server_calls_total{grpc_method=\"/order.v1.OrderService/GetOrderDetails\",grpc_code=\"OK\"} 0",
            body
        );
        Assert.Contains(
            "kinetix_grpc_client_calls_total{peer=\"kinetix-pricing-service\","
                + "grpc_method=\"/pricing.v1.PricingService/CalculatePrice\",grpc_code=\"OK\"} 0",
            body
        );
    }

    [Fact]
    public async Task AScrapeThatCouldNotBeCollectedDoesNotAnswerLikeAnEmptyOne() {
        await using var app = Build(
            Metrics.NewCustomRegistry(),
            collect: (_, _) => throw new InvalidOperationException("a collector could not be read")
        );
        await app.StartAsync();

        using var client = new HttpClient();
        using var response = await client.GetAsync($"{AddressOf(app)}{KinetixMetrics.MetricsPath}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("kinetix_http_requests_total", body);
        Assert.Contains("could not be collected", body);

        await app.StopAsync();
    }

    private static void AssertNoIdentifierReachedALabel(string body) {
        foreach (Match label in LabelValue.Matches(body)) {
            Assert.False(
                LooksLikeAnIdentifier.IsMatch(label.Value),
                $"an identifier reached a metric label: {label.Value}"
            );
        }

        Assert.False(RouteCarryingAnId.IsMatch(body), "a route label carries an id, not a template");
    }

    private static async Task<string> ScrapeAfter(Func<HttpClient, string, Task> traffic) {
        var registry = Metrics.NewCustomRegistry();

        await using var app = Build(registry);
        await app.StartAsync();

        var baseAddress = AddressOf(app);
        using var client = new HttpClient();

        await traffic(client, baseAddress);
        var body = await client.GetStringAsync($"{baseAddress}{KinetixMetrics.MetricsPath}");

        await app.StopAsync();
        return body;
    }

    private static WebApplication Build(
        CollectorRegistry registry, Func<Stream, CancellationToken, Task>? collect = null
    ) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var metrics = new KinetixMetrics(Metrics.WithCustomRegistry(registry), BuildVersion.Of(null));
        metrics.RegisterGrpcServerSurface(global::Order.V1.OrderService.Descriptor);
        metrics.RegisterGrpcClientSurface(
            "kinetix-pricing-service", global::Pricing.V1.PricingService.Descriptor);
        builder.Services.AddSingleton(metrics);

        var app = builder.Build();

        app.UseMiddleware<HttpMetricsMiddleware>();

        app.MapGet("/api/v1/orders/{orderId:guid}", (Guid orderId) => Results.Ok(new { orderId }));
        MetricsEndpoint.Map(app, collect ?? registry.CollectAndExportAsTextAsync);

        return app;
    }

    private static string AddressOf(WebApplication app) {
        return app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First()
            .TrimEnd('/');
    }
}
