using System.Reflection;
using System.Security.Claims;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Kinetix.OrderService.Tests;

public class OrderPoliciesTests {
    private static IAuthorizationService Authorization() {
        var services = new ServiceCollection().AddLogging();
        services.AddAuthorizationBuilder().AddOrderPolicies();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal Staff(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Bearer"));

    [Theory]
    [InlineData("admin", true)]
    [InlineData("operator", false)]
    [InlineData("merchant", false)]
    [InlineData("customer", false)]
    public async Task OnlyAnAdminMayPayAMerchantOut(string role, bool allowed) {
        var result = await Authorization().AuthorizeAsync(Staff(role), null, OrderPolicies.MerchantPayout);

        Assert.Equal(allowed, result.Succeeded);
    }

    [Fact]
    public void CompletingAnOrderRequiresTheMerchantPayoutPolicy() {
        var attribute = typeof(OrderCompletionController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(OrderPolicies.MerchantPayout, attribute.Policy);
    }
}
