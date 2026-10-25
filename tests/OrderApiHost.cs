using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.Infrastructure.Http;
using Kinetix.OrderService.Infrastructure.Observability;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Kinetix.OrderService.Tests;

public sealed class OrderApiHost : IAsyncDisposable {
    private readonly WebApplication _app;

    private OrderApiHost(WebApplication app, string address, CollectorRegistry metrics) {
        _app = app;
        Address = address;
        Metrics = metrics;
    }

    public string Address { get; }

    public CollectorRegistry Metrics { get; }

    public async Task<string> ScrapeAsync() {
        using var exposition = new MemoryStream();
        await Metrics.CollectAndExportAsTextAsync(exposition);
        return System.Text.Encoding.UTF8.GetString(exposition.ToArray());
    }

    public static Task<OrderApiHost> StartAsync(string jwksUrl, Action<IServiceCollection> register) =>
        StartAsync(jwksUrl, register, _ => { });

    public static async Task<OrderApiHost> StartAsync(
        string jwksUrl,
        Action<IServiceCollection> register,
        Action<KestrelServerOptions> kestrel
    ) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(kestrel);
        builder.Configuration["IDENTITY_JWKS_URL"] = jwksUrl;

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddAccessTokens(TestIdentityIssuer.Issuer, TestIdentityIssuer.Audience);
        builder.Services.AddAuthorizationBuilder().AddOrderPolicies();

        var registry = Prometheus.Metrics.NewCustomRegistry();
        builder.Services.AddSingleton(
            new KinetixMetrics(Prometheus.Metrics.WithCustomRegistry(registry), BuildVersion.Of(null))
        );
        builder.Services.AddSingleton<AdminAudit>();

        builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
        builder.Services.AddProblemDetails();
        builder.Services.AddOrderControllers().AddApplicationPart(typeof(OrderController).Assembly);
        register(builder.Services);

        var app = builder.Build();
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First()
            .TrimEnd('/');

        return new OrderApiHost(app, address, registry);
    }

    public async ValueTask DisposeAsync() {
        await _app.DisposeAsync();
    }
}
