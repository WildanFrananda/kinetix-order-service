using System.Reflection;
using System.Security.Claims;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.DTOs.Responses;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;

namespace Kinetix.OrderService.Tests;

public class OrderReadAccessTests {
    private const string Owner = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Stranger = "2b7e6c10-5a3f-4d8e-b1c9-7f0a3e2d9c44";

    private static readonly Guid OrderId = Guid.Parse("5c0f2a8e-9d41-4b7a-8e3c-1f6d2b9a7e05");

    private static OrderResponse AnOrderOf(string customerPrincipalId) => new(
        OrderId,
        "ORD-20261009-0001",
        customerPrincipalId,
        "PAID",
        150000m,
        0m,
        null,
        165000m,
        "Jl. Sudirman No. 45, Jakarta",
        "KINETIX_REGULAR",
        15000m,
        0m,
        15000m,
        4.2,
        "MATCHING_QUOTE",
        DateTime.UtcNow,
        []
    );

    private static OrderController ControllerFor(ClaimsPrincipal caller, OrderResponse? stored) {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.GetOrderByIdAsync(OrderId)).ReturnsAsync(stored);

        return new OrderController(orders.Object) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } }
        };
    }

    private static ClaimsPrincipal Caller(string subject, params string[] roles) {
        var claims = new List<Claim> { new("sub", subject) };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "sub", "role"));
    }

    [Fact]
    public async Task TheOwnerReadsTheirOrder() {
        var response = await ControllerFor(Caller(Owner), AnOrderOf(Owner)).GetOrderById(OrderId);

        Assert.IsType<OkObjectResult>(response);
    }

    [Fact]
    public async Task AnotherCustomerGetsTheSameAnswerAsAnOrderThatDoesNotExist() {
        var foreign = await ControllerFor(Caller(Stranger), AnOrderOf(Owner)).GetOrderById(OrderId);
        var missing = await ControllerFor(Caller(Stranger), null).GetOrderById(OrderId);

        var refused = Assert.IsType<NotFoundObjectResult>(foreign);
        var absent = Assert.IsType<NotFoundObjectResult>(missing);
        Assert.Equal(absent.Value!.ToString(), refused.Value!.ToString());
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task StaffMayReadAnyOrder(string role) {
        var response = await ControllerFor(Caller(Stranger, role), AnOrderOf(Owner)).GetOrderById(OrderId);

        Assert.IsType<OkObjectResult>(response);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("seller")]
    [InlineData("courier")]
    public async Task NoOtherRoleOpensSomeoneElsesOrder(string role) {
        var response = await ControllerFor(Caller(Stranger, role), AnOrderOf(Owner)).GetOrderById(OrderId);

        Assert.IsType<NotFoundObjectResult>(response);
    }

    [Fact]
    public void NoEndpointLetsACallerSetAnOrdersStatus() {
        var endpoints = typeof(OrderController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .SelectMany(attribute => attribute.HttpMethods.Select(verb => $"{verb} {attribute.Template}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["GET my-orders", "GET {orderId:guid}", "POST checkout"], endpoints);
    }
}
