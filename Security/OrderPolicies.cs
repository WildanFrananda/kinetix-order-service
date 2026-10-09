using Kinetix.OrderService.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Kinetix.OrderService.Security;

public static class OrderPolicies {
    public const string MerchantPayout = "MerchantPayout";

    public static AuthorizationBuilder AddOrderPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddPolicy(SagaAdminController.SagaOperatorPolicy, policy =>
                policy.RequireAuthenticatedUser().RequireRole(StaffRoles.All)
            )
            .AddPolicy(MerchantPayout, policy =>
                policy.RequireAuthenticatedUser().RequireRole(StaffRoles.Admin)
            );
}
