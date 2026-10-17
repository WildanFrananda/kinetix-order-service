using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Kinetix.OrderService.Tests;

public static class SignedIn {
    public static ControllerContext As(string? principalId) {
        var claims = principalId is null ? [] : new[] { new Claim("sub", principalId) };

        return new ControllerContext {
            HttpContext = new DefaultHttpContext {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            },
        };
    }
}
