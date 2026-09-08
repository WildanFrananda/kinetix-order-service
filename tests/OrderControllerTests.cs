using System.Security.Claims;
using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.DTOs.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class OrderControllerTests {
    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";

    private static OrderController ControllerRefusingWith(Exception failure) {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.CheckoutAsync(It.IsAny<string>(), It.IsAny<CheckoutRequest>(), It.IsAny<string?>()))
            .ThrowsAsync(failure);

        return new OrderController(orders.Object) {
            ControllerContext = new ControllerContext {
                HttpContext = new DefaultHttpContext {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Customer)], "test"))
                }
            }
        };
    }

    private static async Task<ObjectResult> CheckoutRefusal(Exception failure) {
        var response = await ControllerRefusingWith(failure)
            .Checkout(new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null));

        return Assert.IsAssignableFrom<ObjectResult>(response);
    }

    private static string Text(object body, string property) =>
        body.GetType().GetProperty(property)?.GetValue(body)?.ToString() ?? string.Empty;

    private static IReadOnlyList<string> List(object body, string property) =>
        (IReadOnlyList<string>)(body.GetType().GetProperty(property)?.GetValue(body)
            ?? Array.Empty<string>());

    private static bool Has(object body, string property) =>
        body.GetType().GetProperty(property) is not null;

    [Fact]
    public async Task MatchingBeingDownIs503() {
        var refusal = await CheckoutRefusal(
            new ShippingUnavailableException(new RpcException(new Status(StatusCode.Unavailable, "no route")))
        );

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refusal.StatusCode);
        Assert.Equal("SHIPPING_UNAVAILABLE", Text(refusal.Value!, "error"));
    }

    [Fact]
    public async Task AnUnreadableQuoteIs502AndClaimsNothingAboutTheAddress() {
        var refusal = await CheckoutRefusal(
            new ShippingQuoteMalformedException("the response carried no courier options at all")
        );

        Assert.Equal(StatusCodes.Status502BadGateway, refusal.StatusCode);
        Assert.Equal("SHIPPING_QUOTE_MALFORMED", Text(refusal.Value!, "error"));

        var message = Text(refusal.Value!, "message");
        Assert.DoesNotContain("address", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no courier serves", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fault on our side", message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("courier options", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARealRefusalIs422AndLabelsMatchingsReasonsAsTheRateCards() {
        var refusal = await CheckoutRefusal(new ShippingNotServiceableException([
            "KINETIX_REGULAR: Distance exceeds 500km limit",
        ]));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refusal.StatusCode);
        Assert.Equal("SHIPPING_NO_SERVICE", Text(refusal.Value!, "error"));
        Assert.DoesNotContain("address", Text(refusal.Value!, "message"), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("KINETIX_REGULAR: Distance exceeds 500km limit",
            List(refusal.Value!, "rateCardReasons")
        );
        Assert.Contains("0 km and 0 kg", Text(refusal.Value!, "basis"));
    }

    [Fact]
    public async Task AProbeDerivedTierRefusalIs422AndDoesNotQuoteTheRateCardAsThisOrdersReason() {
        var refusal = await CheckoutRefusal(new ShippingTierNotEstablishedException(
            "KINETIX_CARGO", "Cargo is reserved for packages >= 10kg",
            ["KINETIX_REGULAR", "KINETIX_SAMEDAY"]
        ));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refusal.StatusCode);
        Assert.Equal("SHIPPING_TIER_NOT_ESTABLISHED", Text(refusal.Value!, "error"));

        var message = Text(refusal.Value!, "message");
        Assert.DoesNotContain("Cargo is reserved", message);
        Assert.Contains("could not be confirmed", message);

        Assert.Equal("Cargo is reserved for packages >= 10kg", Text(refusal.Value!, "rateCardReason"));
        Assert.Contains("0 km and 0 kg", Text(refusal.Value!, "basis"));
        Assert.Contains("KINETIX_REGULAR", List(refusal.Value!, "availableTiers"));
    }

    [Fact]
    public async Task AnUnknownTierIs400AndCarriesNoRateCardBasis() {
        var refusal = await CheckoutRefusal(new ShippingTierUnknownException(
            "KINETIX_TELEPORT", ["KINETIX_REGULAR"]
        ));

        Assert.Equal(StatusCodes.Status400BadRequest, refusal.StatusCode);
        Assert.Equal("SHIPPING_TIER_UNKNOWN", Text(refusal.Value!, "error"));
        Assert.Contains("KINETIX_TELEPORT", Text(refusal.Value!, "message"));
        Assert.False(Has(refusal.Value!, "basis"));
    }

    [Fact]
    public async Task AContradictedFeeIs502UnderItsOwnCode() {
        var refusal = await CheckoutRefusal(new ShippingFeeContradictedException(9000m, 0m, 0m));

        Assert.Equal(StatusCodes.Status502BadGateway, refusal.StatusCode);
        Assert.Equal("SHIPPING_FEE_CONTRADICTED", Text(refusal.Value!, "error"));
    }

    [Fact]
    public async Task NoTwoShippingFailuresRenderTheSameWay() {
        var refusals = new Exception[] {
            new ShippingUnavailableException(new RpcException(new Status(StatusCode.Unavailable, "no route"))),
            new ShippingQuoteMalformedException("the response carried no courier options at all"),
            new ShippingNotServiceableException(["KINETIX_REGULAR: Distance exceeds 500km limit"]),
            new ShippingTierNotEstablishedException("KINETIX_CARGO", "Cargo is reserved for packages >= 10kg", []),
            new ShippingTierUnknownException("KINETIX_TELEPORT", []),
            new ShippingFeeContradictedException(9000m, 0m, 0m),
        };

        var rendered = new List<string>();
        foreach (var failure in refusals) {
            var refusal = await CheckoutRefusal(failure);
            rendered.Add($"{refusal.StatusCode} {Text(refusal.Value!, "error")}");
        }

        Assert.Equal(rendered.Count, rendered.Distinct().Count());
        Assert.Equal([
            "503 SHIPPING_UNAVAILABLE",
            "502 SHIPPING_QUOTE_MALFORMED",
            "422 SHIPPING_NO_SERVICE",
            "422 SHIPPING_TIER_NOT_ESTABLISHED",
            "400 SHIPPING_TIER_UNKNOWN",
            "502 SHIPPING_FEE_CONTRADICTED",
        ], rendered);
    }
}
