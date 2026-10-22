using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kinetix.OrderService.Tests;

public sealed class OrderApiHost : IAsyncDisposable {
    private readonly WebApplication _app;

    private OrderApiHost(WebApplication app, string address) {
        _app = app;
        Address = address;
    }

    public string Address { get; }

    public static async Task<OrderApiHost> StartAsync(string jwksUrl, Action<IServiceCollection> register) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["IDENTITY_JWKS_URL"] = jwksUrl;

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddAccessTokens(TestIdentityIssuer.Issuer, TestIdentityIssuer.Audience);
        builder.Services.AddAuthorizationBuilder().AddOrderPolicies();
        builder.Services.AddControllers().AddApplicationPart(typeof(OrderController).Assembly);
        register(builder.Services);

        var app = builder.Build();
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

        return new OrderApiHost(app, address);
    }

    public async ValueTask DisposeAsync() {
        await _app.DisposeAsync();
    }
}
