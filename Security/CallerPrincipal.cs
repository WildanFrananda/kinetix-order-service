using System.Security.Claims;

namespace Kinetix.OrderService.Security;

public static class CallerPrincipal {
    public static string? Of(ClaimsPrincipal user) {
        var subject = user.FindFirst(AccessTokenValidation.SubjectClaim)?.Value;
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }
}
