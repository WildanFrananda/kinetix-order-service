using System.Security.Claims;

namespace Kinetix.OrderService.Security;

public static class StaffRoles {
    public static readonly string[] All = ["operator", "admin"];

    public static bool Includes(ClaimsPrincipal caller) => All.Any(caller.IsInRole);
}
