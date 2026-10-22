using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kinetix.OrderService.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Kinetix.OrderService.Tests;

public class JwksKeyProviderTests {
    private sealed class ShiftingClock : TimeProvider {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Identity : HttpMessageHandler {
        public List<string> Kids { get; } = ["k1"];

        public bool Down { get; set; }

        public int Fetches { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Fetches += 1;

            if (Down) {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var keys = string.Join(",", Kids.Select(Jwk));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent($"{{\"keys\":[{keys}]}}", Encoding.UTF8, "application/json"),
            });
        }

        private static string Jwk(string kid) {
            using var rsa = RSA.Create(2048);
            var parameters = rsa.ExportParameters(false);
            var n = Base64UrlEncoder.Encode(parameters.Modulus);
            var e = Base64UrlEncoder.Encode(parameters.Exponent);

            return $"{{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\","
                + $"\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}";
        }
    }

    private sealed class World {
        public Identity Identity { get; } = new();

        public ShiftingClock Clock { get; } = new();

        public JwksKeyProvider Provider { get; }

        public World() {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["IDENTITY_JWKS_URL"] = "http://identity.test/.well-known/jwks.json",
                })
                .Build();
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(Identity));

            Provider = new JwksKeyProvider(config, factory.Object, Clock, NullLogger<JwksKeyProvider>.Instance);
        }

        public IEnumerable<SecurityKey> Resolve(string kid) =>
            Provider.Resolve("token", null!, kid, new TokenValidationParameters());

        public void Wait(TimeSpan span) => Clock.Now += span;
    }

    private static async Task<World> Started() {
        var world = new World();
        await world.Provider.RefreshAsync();
        return world;
    }

    [Fact]
    public async Task AKnownKeyIsAnsweredWithoutAskingIdentity() {
        var world = await Started();

        Assert.Single(world.Resolve("k1"));
        Assert.Single(world.Resolve("k1"));
        Assert.Equal(1, world.Identity.Fetches);
    }

    [Fact]
    public async Task UnknownKeysDoNotMakeIdentityAnswerEveryRequest() {
        var world = await Started();

        for (var i = 0; i < 50; i++) {
            Assert.Empty(world.Resolve($"forged-{i}"));
        }

        Assert.Equal(1, world.Identity.Fetches);
    }

    [Fact]
    public async Task AnUnknownKeyIsLookedForAgainOnceTheIntervalHasPassed() {
        var world = await Started();
        world.Identity.Kids.Add("k2");
        world.Wait(JwksKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));

        Assert.Single(world.Resolve("k2"));
        Assert.Equal(2, world.Identity.Fetches);

        Assert.Empty(world.Resolve("forged"));
        Assert.Equal(2, world.Identity.Fetches);
    }

    [Fact]
    public async Task AKeyPublishedJustAfterARefreshWaitsForTheInterval() {
        var world = await Started();
        world.Identity.Kids.Add("k2");
        world.Wait(TimeSpan.FromSeconds(5));

        Assert.Empty(world.Resolve("k2"));

        world.Wait(TimeSpan.FromSeconds(6));

        Assert.Single(world.Resolve("k2"));
        Assert.Equal(2, world.Identity.Fetches);
    }

    [Fact]
    public async Task AFailedRefreshIsNotRetriedOnEveryRequest() {
        var world = await Started();
        world.Identity.Down = true;
        world.Wait(JwksKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));

        Assert.Empty(world.Resolve("k2"));
        Assert.Empty(world.Resolve("k3"));
        Assert.Equal(2, world.Identity.Fetches);
        Assert.Single(world.Resolve("k1"));
    }

    [Fact]
    public async Task AnUnknownKeyWhileIdentityAnswersIsAnInvalidTokenNotAnOutage() {
        var world = await Started();
        world.Wait(JwksKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));

        Assert.Empty(world.Resolve("forged"));
        Assert.False(world.Provider.CannotVouchFor("forged"));
    }

    [Fact]
    public async Task AnUnknownKeyWhileIdentityCannotBeAskedIsAnOutage() {
        var world = await Started();
        world.Identity.Down = true;
        world.Wait(JwksKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));

        Assert.Empty(world.Resolve("k2"));
        Assert.True(world.Provider.CannotVouchFor("k2"));
        Assert.False(world.Provider.CannotVouchFor("k1"));
        Assert.False(world.Provider.CannotVouchFor(null));
    }

    [Fact]
    public async Task TheOutageEndsWithTheFirstRefreshIdentityAnswers() {
        var world = new World();
        world.Identity.Down = true;
        await Assert.ThrowsAnyAsync<Exception>(() => world.Provider.RefreshAsync());

        Assert.True(world.Provider.CannotVouchFor("k1"));

        world.Identity.Down = false;
        world.Wait(JwksKeyProvider.UnknownKeyRefreshInterval + TimeSpan.FromSeconds(1));

        Assert.Single(world.Resolve("k1"));
        Assert.False(world.Provider.CannotVouchFor("k1"));
        Assert.False(world.Provider.CannotVouchFor("forged"));
    }

    [Fact]
    public async Task ATokenWithNoKeyIdResolvesToNoKeyRatherThanThrowing() {
        var world = await Started();

        Assert.Empty(world.Resolve(null!));
    }
}
