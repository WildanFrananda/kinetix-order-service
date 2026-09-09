using System.Net;
using Kinetix.OrderService.Infrastructure.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class DrainTests {
    [Fact]
    public async Task ARequestThatIsAlreadyRunningIsAllowedToFinish() {
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        await using var app = Build(async () => {
            reached.TrySetResult();
            await release.Task;
            return "finished";
        });
        await app.StartAsync();

        using var client = new HttpClient();
        var inFlight = client.GetAsync($"{AddressOf(app)}/slow");
        await reached.Task;

        var stopping = app.StopAsync();
        await Task.Delay(200);
        release.SetResult();

        using var response = await inFlight;
        await stopping;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("finished", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoNewConnectionIsAcceptedOnceTheDrainHasStarted() {
        await using var app = Build(() => Task.FromResult("finished"));
        await app.StartAsync();

        var address = AddressOf(app);
        await app.StopAsync();

        using var client = new HttpClient();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"{address}/slow"));
    }

    [Fact]
    public async Task ReadinessSaysDrainingTheMomentTheHostBeginsStopping() {
        var drain = new DrainState();

        Assert.False(drain.IsDraining);

        await using var app = Build(() => Task.FromResult("finished"));
        app.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(drain.Begin);

        await app.StartAsync();
        Assert.False(drain.IsDraining);

        await app.StopAsync();
        Assert.True(drain.IsDraining);
    }

    private static WebApplication Build(Func<Task<string>> handler) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.MapGet("/slow", handler);

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
