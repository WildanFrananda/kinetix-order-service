using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Kinetix.OrderService.Security;

public static class AccessTokenValidation {
    public const string TokenUseClaim = "token_use";
    public const string AccessTokenUse = "access";

    public static void Apply(JwtBearerOptions options, string issuer, string audience) {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RoleClaimType = "role",
        };
        options.Events = new JwtBearerEvents {
            OnTokenValidated = context => {
                if (context.Principal?.FindFirst(TokenUseClaim)?.Value != AccessTokenUse) {
                    context.Fail("only an access token is accepted here");
                }
                return Task.CompletedTask;
            },
        };
    }
}
