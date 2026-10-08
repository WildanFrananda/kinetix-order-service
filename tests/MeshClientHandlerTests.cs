using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kinetix.OrderService.Tests;

public class MeshClientHandlerTests {
    private static bool Accepts(ServiceIdentity identity, X509Certificate2 server, string expectedService) {
        using var handler = MeshClientHandler.For(identity, expectedService, NullLogger.Instance);
        return handler.SslOptions.RemoteCertificateValidationCallback!(new object(), server, null, SslPolicyErrors.None);
    }

    [Fact]
    public void TheServiceThatWasDialledIsAccepted() {
        using var pki = new TestPki();

        Assert.True(Accepts(pki.IdentityFor("order"), pki.Leaf("pricing"), "pricing"));
    }

    [Fact]
    public void AnotherServiceFromTheSameCaCannotAnswerForIt() {
        using var pki = new TestPki();

        Assert.False(Accepts(pki.IdentityFor("order"), pki.Leaf("payment"), "pricing"));
    }

    [Fact]
    public void ACertificateNamingItFromAnotherCaIsRefused() {
        using var pki = new TestPki();
        using var forger = new TestPki("Not Kinetix");

        Assert.False(Accepts(pki.IdentityFor("order"), forger.Leaf("pricing"), "pricing"));
    }

    [Fact]
    public void ACertificateCarryingNoSpiffeIdentityIsRefused() {
        using var pki = new TestPki();

        Assert.False(Accepts(pki.IdentityFor("order"), pki.Leaf("pricing", spiffeId: null), "pricing"));
    }

    [Fact]
    public void AServiceWhoseNameMerelyStartsWithTheExpectedOneIsRefused() {
        using var pki = new TestPki();

        Assert.False(Accepts(
            pki.IdentityFor("order"),
            pki.Leaf("pricing-admin"),
            "pricing"
        ));
    }

    [Fact]
    public async Task AHandshakeWithTheWrongServiceFailsAndTheRightOneSucceeds() {
        using var pki = new TestPki();
        var identity = pki.IdentityFor("order");

        await using (var impostor = await ServerPresenting(pki.Leaf("payment"))) {
            using var client = new HttpClient(MeshClientHandler.For(identity, "pricing", NullLogger.Instance));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(impostor.Urls.First()));
        }

        await using var pricing = await ServerPresenting(pki.Leaf("pricing"));
        using var trusted = new HttpClient(MeshClientHandler.For(identity, "pricing", NullLogger.Instance));
        using var response = await trusted.GetAsync(pricing.Urls.First());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<WebApplication> ServerPresenting(X509Certificate2 certificate) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate))
        );

        var app = builder.Build();
        app.MapGet("/", () => Results.Ok());
        await app.StartAsync();
        return app;
    }
}
