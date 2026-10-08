using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

public class AccessTokenValidationTests {
    private const string Issuer = "https://identity.kinetix.test";
    private const string Audience = "kinetix";

    [Theory]
    [InlineData("access", HttpStatusCode.OK)]
    [InlineData("refresh", HttpStatusCode.Unauthorized)]
    [InlineData("mfa_challenge", HttpStatusCode.Unauthorized)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task OnlyAnAccessTokenOpensAnAuthenticatedRoute(string? tokenUse, HttpStatusCode expected) {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test" };

        await using var app = await ServiceAccepting(key);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{AddressOf(app)}/api/v1/orders/my-orders");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Mint(key, tokenUse));

        using var response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    private static string Mint(SecurityKey key, string? tokenUse) {
        var claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString() };
        if (tokenUse is not null) {
            claims[AccessTokenValidation.TokenUseClaim] = tokenUse;
        }

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    private static async Task<WebApplication> ServiceAccepting(SecurityKey key) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => {
                AccessTokenValidation.Apply(options, Issuer, Audience);
                options.TokenValidationParameters.IssuerSigningKey = key;
            });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/v1/orders/my-orders", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync();
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
