using System.Security.Claims;

namespace Kinetix.OrderService.Security;

public static class StaffRoles {
    public const string Admin = "admin";

    public static bool Includes(ClaimsPrincipal caller) => caller.IsInRole(Admin);
}
