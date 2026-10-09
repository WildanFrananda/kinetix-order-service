using System.Security.Claims;

namespace Kinetix.OrderService.Security;

public static class StaffRoles {
    public const string Admin = "admin";

    public static readonly string[] All = ["operator", Admin];

    public static bool Includes(ClaimsPrincipal caller) => All.Any(caller.IsInRole);
}
