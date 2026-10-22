using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kinetix.OrderService.Tests;

public sealed class TestIdentityIssuer : IAsyncDisposable {
    public const string Issuer = "https://identity.kinetix.test";
    public const string Audience = "kinetix";
    public const string KeyId = "identity-test-key";

    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplication? _jwks;

    public string JwksUrl { get; private set; } = string.Empty;

    public static async Task<TestIdentityIssuer> PublishingAsync() {
        var issuer = new TestIdentityIssuer();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.MapGet("/.well-known/jwks.json", () => Results.Json(issuer.Jwks()));
        await app.StartAsync();

        issuer._jwks = app;
        issuer.JwksUrl = $"{AddressOf(app)}/.well-known/jwks.json";
        return issuer;
    }

    public static TestIdentityIssuer Unreachable() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return new TestIdentityIssuer { JwksUrl = $"http://127.0.0.1:{port}/.well-known/jwks.json" };
    }

    public string AccessToken(string principalId, string role) =>
        Mint(new RsaSecurityKey(_rsa) { KeyId = KeyId }, principalId, role);

    public static string ForgedAccessToken(string principalId, string role) {
        using var stranger = RSA.Create(2048);
        return Mint(new RsaSecurityKey(stranger.ExportParameters(true)) { KeyId = "unknown-key" }, principalId, role);
    }

    private static string Mint(SecurityKey key, string principalId, string role) {
        var now = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            Claims = new Dictionary<string, object> {
                ["sub"] = principalId,
                [AccessTokenValidation.TokenUseClaim] = AccessTokenValidation.AccessTokenUse,
                ["uid"] = 42,
                ["email"] = "someone@kinetix.test",
                ["role"] = role,
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    private object Jwks() {
        var parameters = _rsa.ExportParameters(false);

        return new {
            keys = new[] {
                new {
                    kty = "RSA",
                    kid = KeyId,
                    alg = "RS256",
                    use = "sig",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!),
                },
            },
        };
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

    public async ValueTask DisposeAsync() {
        if (_jwks is not null) {
            await _jwks.DisposeAsync();
        }

        _rsa.Dispose();
    }
}
