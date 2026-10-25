using System.Security.Claims;
using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.DTOs.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class OrderControllerTests {
    private const string Buyer = "Test Buyer";
    private const string BuyerPhone = "081200000000";

    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";

    private static OrderController ControllerRefusingWith(Exception failure) {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.CheckoutAsync(It.IsAny<string>(), It.IsAny<CheckoutRequest>(), It.IsAny<string?>()))
            .ThrowsAsync(failure);

        return new OrderController(orders.Object, TestAudit.Silent()) {
            ControllerContext = new ControllerContext {
                HttpContext = new DefaultHttpContext {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Customer)], "test"))
                }
            }
        };
    }

    private static async Task<ObjectResult> CheckoutRefusal(Exception failure) {
        var response = await ControllerRefusingWith(failure)
            .Checkout(new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone));

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
    public async Task AMerchantThatMayNotSellIs409() {
        var refusal = await CheckoutRefusal(new MerchantMayNotSellException("merchant-1"));

        Assert.Equal(StatusCodes.Status409Conflict, refusal.StatusCode);
        Assert.Equal("MERCHANT_NOT_TRADING", Text(refusal.Value!, "error"));
    }

    [Fact]
    public async Task IdentityNotSayingWhetherTheMerchantMaySellIs503() {
        var refusal = await CheckoutRefusal(
            new MerchantStandingUnknownException(
                "merchant-1",
                new RpcException(new Status(StatusCode.Unavailable, "down"))
            )
        );

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refusal.StatusCode);
        Assert.Equal("IDENTITY_UNAVAILABLE", Text(refusal.Value!, "error"));
    }

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
    public async Task AnEveryTierUnavailableAnswerIsAFaultAndMakesNoCourierClaim() {
        var refusal = await CheckoutRefusal(new ShippingQuoteMalformedException(
            "every tier came back unavailable (KINETIX_REGULAR: Distance exceeds 500km limit), and "
          + "this quote was asked at 0 km and 0 g"
        ));

        Assert.Equal(StatusCodes.Status502BadGateway, refusal.StatusCode);
        Assert.Equal("SHIPPING_QUOTE_MALFORMED", Text(refusal.Value!, "error"));

        var message = Text(refusal.Value!, "message");
        Assert.DoesNotContain("no courier", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot carry", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("can carry", message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Distance exceeds 500km limit", message);
        Assert.False(Has(refusal.Value!, "rateCardReasons"));
    }

    [Fact]
    public async Task AnUnchargeableSetOfAmountsIs502AndKeepsTheArithmeticOutOfTheBody() {
        var refusal = await CheckoutRefusal(new OrderAmountsUnchargeableException(
            "the total to escrow (1) is not what that escrow would split into"
        ));

        Assert.Equal(StatusCodes.Status502BadGateway, refusal.StatusCode);
        Assert.Equal("ORDER_AMOUNTS_UNCHARGEABLE", Text(refusal.Value!, "error"));

        var message = Text(refusal.Value!, "message");
        Assert.Contains("fault on our side", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("split into", message);
    }

    [Fact]
    public async Task ACartSpanningTwoMerchantsIsRefusedWithACodeTheStorefrontCanActOn() {
        var refusal = await CheckoutRefusal(new CartSpansTwoMerchantsException(2));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refusal.StatusCode);
        Assert.Equal("CART_SPANS_TWO_MERCHANTS", Text(refusal.Value!, "error"));

        Assert.Contains("separately", Text(refusal.Value!, "message"));
    }

    [Fact]
    public async Task AnEmptyCartAndAMerchantlessProductDoNotShareAnErrorCode() {
        var empty = await CheckoutRefusal(new EmptyCartException());
        var merchantless = await CheckoutRefusal(new ProductHasNoMerchantException("PRODUCT-02"));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, empty.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, merchantless.StatusCode);

        Assert.Equal("CART_EMPTY", Text(empty.Value!, "error"));
        Assert.Equal("PRODUCT_HAS_NO_MERCHANT", Text(merchantless.Value!, "error"));
        Assert.Equal("PRODUCT-02", Text(merchantless.Value!, "productId"));
        Assert.Contains("no merchant", Text(merchantless.Value!, "message"));
    }

    [Fact]
    public async Task ATierRefusalIs422AndCarriesTheFleetsReasonApartFromTheMessage() {
        var refusal = await CheckoutRefusal(new ShippingTierNotEstablishedException(
            "KINETIX_CARGO",
            "Cargo is reserved for packages >= 10kg",
            ["KINETIX_REGULAR", "KINETIX_SAMEDAY"]
        ));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refusal.StatusCode);
        Assert.Equal("SHIPPING_TIER_NOT_ESTABLISHED", Text(refusal.Value!, "error"));

        var message = Text(refusal.Value!, "message");
        Assert.DoesNotContain("Cargo is reserved", message);
        Assert.Contains("could not be confirmed", message);

        Assert.Equal("Cargo is reserved for packages >= 10kg", Text(refusal.Value!, "rateCardReason"));
        Assert.False(Has(refusal.Value!, "basis"));
        Assert.Contains("KINETIX_REGULAR", List(refusal.Value!, "availableTiers"));
    }

    [Theory]
    [InlineData(AddressPlacementFailure.NotFound, "NOT_FOUND")]
    [InlineData(AddressPlacementFailure.Ambiguous, "AMBIGUOUS")]
    [InlineData(AddressPlacementFailure.OutOfServiceArea, "OUT_OF_SERVICE_AREA")]
    [InlineData(AddressPlacementFailure.Insufficient, "INSUFFICIENT_ADDRESS")]
    [InlineData(AddressPlacementFailure.TooVague, "TOO_VAGUE")]
    public async Task AnAddressThatCannotBePlacedSaysWhy(AddressPlacementFailure failure, string reason) {
        var refusal = await CheckoutRefusal(new ShippingAddressUnplacedException(failure, "identity's detail"));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refusal.StatusCode);
        Assert.Equal(reason, Text(refusal.Value!, "reason"));
        Assert.Contains("nothing has been charged", Text(refusal.Value!, "message"));
    }

    [Fact]
    public async Task AnUnknownTierIs400AndCarriesNoRateCardBasis() {
        var refusal = await CheckoutRefusal(new ShippingTierUnknownException(
            "KINETIX_TELEPORT",
            ["KINETIX_REGULAR"]
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
    public async Task NoTwoCheckoutFailuresRenderTheSameWay() {
        var refusals = new Exception[] {
            new PricingUnavailableException(new RpcException(new Status(StatusCode.Unavailable, "no route"))),
            new ShippingUnavailableException(new RpcException(new Status(StatusCode.Unavailable, "no route"))),
            new ShippingQuoteMalformedException("the response carried no courier options at all"),
            new ShippingTierNotEstablishedException("KINETIX_CARGO", "Cargo is reserved for packages >= 10kg", []),
            new ShippingTierUnknownException("KINETIX_TELEPORT", []),
            new ShippingFeeContradictedException(9000m, 0m, 0m),
            new OrderAmountsUnchargeableException("the total to escrow (1) is not what it splits into"),
            new CheckoutFailedException("ORD-20260908-0001", "stock for PRODUCT-01: out of stock", true, false, false),
            new CheckoutFailedException("ORD-20260908-0002", "stock for PRODUCT-01: out of stock", false, false, false),
            new CheckoutFailedException("ORD-20260908-0003", "a service did not answer", true, true, false),
            new EmptyCartException(),
            new ProductHasNoMerchantException("PRODUCT-02"),
            new CartSpansTwoMerchantsException(2),
            new ProductWeightUnknownException(["PRODUCT-01"]),
            new MerchantStoreUnplacedException("merchant-1"),
            new ShippingAddressUnplacedException(AddressPlacementFailure.NotFound, "no match"),
            new AddressPlacementUnavailableException("the geocoder is down"),
            new AddressUnknownException("merchant-1", new RpcException(new Status(StatusCode.Unavailable, "no route"))),
            new ShippingNotServiceableException(["KINETIX_REGULAR: Distance exceeds 500km limit"]),
            new RecipientMissingException("shippingAddress"),
            new FieldTooLongException("shippingAddress", 500),
            new IdempotencyKeyReusedException("ORD-20260908-0004"),
            new CheckoutInProgressException("ORD-20260908-0005"),
            new PricingRefusedException("no shipping rate for tier 'KINETIX_CARGO'", new Exception("refused")),
            new OrderTotalNotChargeableException(),
            new OwnProductNotPurchasableException("PRODUCT-01"),
            new CartLineQuantityOutOfRangeException("PRODUCT-01", 0, 1, 999),
            new CartUnavailableException(new Exception("redis is down")),
        };

        var rendered = new List<string>();
        foreach (var failure in refusals) {
            var refusal = await CheckoutRefusal(failure);
            rendered.Add($"{refusal.StatusCode} {Text(refusal.Value!, "error")}");
        }

        Assert.Equal(rendered.Count, rendered.Distinct().Count());
        Assert.Equal(
            [
                "503 PRICING_UNAVAILABLE",
                "503 SHIPPING_UNAVAILABLE",
                "502 SHIPPING_QUOTE_MALFORMED",
                "422 SHIPPING_TIER_NOT_ESTABLISHED",
                "400 SHIPPING_TIER_UNKNOWN",
                "502 SHIPPING_FEE_CONTRADICTED",
                "502 ORDER_AMOUNTS_UNCHARGEABLE",
                "409 CHECKOUT_ROLLED_BACK",
                "409 CHECKOUT_ROLLBACK_PENDING",
                "503 CHECKOUT_DEPENDENCY_UNAVAILABLE",
                "422 CART_EMPTY",
                "422 PRODUCT_HAS_NO_MERCHANT",
                "422 CART_SPANS_TWO_MERCHANTS",
                "422 PRODUCT_WEIGHT_UNKNOWN",
                "422 MERCHANT_STORE_NOT_ON_MAP",
                "422 SHIPPING_ADDRESS_NOT_PLACED",
                "503 ADDRESS_LOOKUP_UNAVAILABLE",
                "503 IDENTITY_UNAVAILABLE",
                "422 SHIPPING_NOT_SERVICEABLE",
                "400 RECIPIENT_REQUIRED",
                "400 FIELD_TOO_LONG",
                "422 IDEMPOTENCY_KEY_REUSED",
                "409 CHECKOUT_IN_PROGRESS",
                "422 PRICING_REFUSED",
                "422 ORDER_TOTAL_NOT_CHARGEABLE",
                "422 OWN_PRODUCT_NOT_PURCHASABLE",
                "422 CART_LINE_QUANTITY_OUT_OF_RANGE",
                "503 CART_UNAVAILABLE",
            ],
            rendered
        );
    }

    [Fact]
    public async Task ARolledBackCheckoutSaysEverythingCameBack() {
        var refusal = await CheckoutRefusal(
            new CheckoutFailedException("ORD-20261021-0001", "stock for PRODUCT-01: out of stock", true, false, false)
        );

        Assert.Equal(StatusCodes.Status409Conflict, refusal.StatusCode);
        Assert.Equal("CHECKOUT_ROLLED_BACK", Text(refusal.Value!, "error"));
        Assert.Equal("True", Text(refusal.Value!, "rolledBack"));
        Assert.Contains("has been given back", Text(refusal.Value!, "message"));
    }

    [Fact]
    public async Task ACheckoutStillUnwindingDoesNotClaimItIsRolledBack() {
        var refusal = await CheckoutRefusal(
            new CheckoutFailedException("ORD-20261021-0002", "stock for PRODUCT-01: out of stock", false, false, false)
        );

        Assert.Equal(StatusCodes.Status409Conflict, refusal.StatusCode);
        Assert.Equal("CHECKOUT_ROLLBACK_PENDING", Text(refusal.Value!, "error"));
        Assert.Equal("False", Text(refusal.Value!, "rolledBack"));
        Assert.Contains("still being given back", Text(refusal.Value!, "message"));
        Assert.DoesNotContain("has been given back", Text(refusal.Value!, "message"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACheckoutADependencyCouldNotAnswerIs503WhateverItsRollback(bool rolledBack) {
        var refusal = await CheckoutRefusal(
            new CheckoutFailedException("ORD-20261021-0003", "a service did not answer", rolledBack, true, false)
        );

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refusal.StatusCode);
        Assert.Equal("CHECKOUT_DEPENDENCY_UNAVAILABLE", Text(refusal.Value!, "error"));
        Assert.Equal(rolledBack.ToString(), Text(refusal.Value!, "rolledBack"));
    }

    [Fact]
    public async Task AReplayedFailureSaysTheKeyIsSpent() {
        var refusal = await CheckoutRefusal(
            new CheckoutFailedException("ORD-20261021-0004", "a service did not answer", true, false, true)
        );

        Assert.Equal(StatusCodes.Status409Conflict, refusal.StatusCode);
        Assert.Equal("CHECKOUT_ROLLED_BACK", Text(refusal.Value!, "error"));
        Assert.Equal("True", Text(refusal.Value!, "replayed"));
        Assert.Contains("new Idempotency-Key", Text(refusal.Value!, "message"));
    }

    [Fact]
    public async Task NoCheckoutRefusalTellsTheCustomerACourierVerdictThatWasNeverEstablished() {
        var refusals = new Exception[] {
            new ShippingUnavailableException(new RpcException(new Status(StatusCode.Unavailable, "no route"))),
            new ShippingQuoteMalformedException("every tier came back unavailable"),
            new ShippingTierNotEstablishedException("KINETIX_CARGO", "Cargo is reserved for packages >= 10kg", []),
            new ShippingTierUnknownException("KINETIX_TELEPORT", []),
            new ShippingFeeContradictedException(9000m, 0m, 0m),
            new OrderAmountsUnchargeableException("the total to escrow (1) is not what it splits into"),
        };

        foreach (var failure in refusals) {
            var refusal = await CheckoutRefusal(failure);
            var message = Text(refusal.Value!, "message");

            Assert.DoesNotContain("no courier", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("carry this order", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("serves", message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
