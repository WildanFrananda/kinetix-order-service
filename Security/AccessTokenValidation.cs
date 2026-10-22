using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kinetix.OrderService.Security;

public static class AccessTokenValidation {
    public const string TokenUseClaim = "token_use";
    public const string AccessTokenUse = "access";
    public const string SubjectClaim = "sub";
    public const string RoleClaim = "role";

    private const string BearerPrefix = "Bearer ";

    public static IServiceCollection AddAccessTokens(this IServiceCollection services, string issuer, string audience) {
        services.AddSingleton<JwksKeyProvider>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => Apply(options, issuer, audience));
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwksKeyProvider>((options, jwks) => {
                options.TokenValidationParameters.IssuerSigningKeyResolver = jwks.Resolve;
            });

        return services;
    }

    public static void Apply(JwtBearerOptions options, string issuer, string audience) {
        options.RequireHttpsMetadata = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            NameClaimType = SubjectClaim,
            RoleClaimType = RoleClaim,
        };
        options.Events = new JwtBearerEvents {
            OnTokenValidated = context => {
                if (context.Principal?.FindFirst(TokenUseClaim)?.Value != AccessTokenUse) {
                    context.Fail("only an access token is accepted here");
                }

                return Task.CompletedTask;
            },
            OnChallenge = async context => {
                if (context.AuthenticateFailure is null) {
                    return;
                }

                var jwks = context.HttpContext.RequestServices.GetService<JwksKeyProvider>();
                if (jwks is null || !jwks.CannotVouchFor(KeyIdOf(context.Request))) {
                    return;
                }

                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new {
                    error = "IDENTITY_UNAVAILABLE",
                    message = "your sign-in cannot be checked right now: identity's signing keys could not be "
                            + "fetched. Nothing was done; try again shortly.",
                });
            },
        };
    }

    private static string? KeyIdOf(HttpRequest request) {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        try {
            return new JsonWebToken(header[BearerPrefix.Length..].Trim()).Kid;
        } catch (Exception e) when (e is ArgumentException or SecurityTokenException) {
            return null;
        }
    }
}
