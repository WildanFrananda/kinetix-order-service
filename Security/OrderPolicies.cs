using Kinetix.OrderService.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Kinetix.OrderService.Security;

public static class OrderPolicies {
    public const string MerchantPayout = "MerchantPayout";

    public static AuthorizationBuilder AddOrderPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddPolicy(SagaAdminController.SagaAdminPolicy, policy =>
                policy.RequireAuthenticatedUser().RequireRole(StaffRoles.Admin)
            )
            .AddPolicy(MerchantPayout, policy =>
                policy.RequireAuthenticatedUser().RequireRole(StaffRoles.Admin)
            );
}
