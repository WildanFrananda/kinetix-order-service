using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Infrastructure.Http;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;
using OrderApplicationService = Kinetix.OrderService.Application.Services.OrderService;

namespace Kinetix.OrderService.Tests;

public class OrderServiceTests {
    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static EstimateShippingResult RateCardFloor() => new(0.0, [
        new ShippingOptionResult("KINETIX_INSTANT", "Kinetix Express Instant", 0.0, 15000m, "1 - 2 Jam", true, null),
        new ShippingOptionResult("KINETIX_SAMEDAY", "Kinetix SameDay", 0.0, 12000m, "6 - 8 Jam", true, null),
        new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, 9000m, "1 - 3 Hari", true, null),
        new ShippingOptionResult("KINETIX_CARGO", "Kinetix Cargo Heavy", 0.0, 25000m, "3 - 5 Hari", false,
            "Cargo is reserved for packages >= 10kg"),
    ]);

    private static Mock<IShippingClient> ShippingReturning(EstimateShippingResult result) {
        var shipping = new Mock<IShippingClient>();
        shipping.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<long>(), It.IsAny<string>()
        )).ReturnsAsync(result);
        return shipping;
    }

    private static Mock<IEscrowClient> AcceptingEscrow() {
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()
        )).ReturnsAsync(StepResult.Ok());
        return escrow;
    }

    private static OrderApplicationService NewOrderService(
        OrderDbContext db,
        ICartService cart,
        IPricingClient pricing,
        IShippingClient shipping,
        IEscrowClient? escrowClient = null
    ) {
        var voucher = new Mock<IVoucherQuotaClient>();
        voucher.Setup(c => c.RedeemVoucherAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var flash = new Mock<IFlashSaleClient>();
        flash.Setup(c => c.AllocateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var stock = new Mock<IStockClient>();
        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var escrow = escrowClient ?? AcceptingEscrow().Object;

        var policy = new CompensationPolicy();
        var runner = new CheckoutSagaRunner(db, voucher.Object, flash.Object, stock.Object, escrow,
            new FakeSagaLeaseStore(db, policy), policy, new RequestIdAccessor(new HttpContextAccessor()),
            NullLogger<CheckoutSagaRunner>.Instance
        );

        return new OrderApplicationService(db, cart, pricing, shipping, runner,
            NullLogger<OrderApplicationService>.Instance
        );
    }

    private static OrderDbContext GetInMemoryDbContext() {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new OrderDbContext(options);
    }

    private static Mock<ICartService> CartWithOneItem() {
        var cartService = new Mock<ICartService>();
        var cart = new CustomerCart(Customer);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "Sample Product",
            UnitPrice = 100000m,
            Quantity = 2,
            MerchantPrincipalId = Merchant
        });
        cartService.Setup(s => s.GetCartAsync(Customer)).ReturnsAsync(cart);
        return cartService;
    }

    private static Mock<IPricingClient> PricingPassingShippingThrough() {
        var pricing = new Mock<IPricingClient>();
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(), It.IsAny<IReadOnlyList<PriceLine>>(), It.IsAny<decimal>()
        )).ReturnsAsync((string? _, IReadOnlyList<PriceLine> _, decimal baseShippingFee) =>
            new PriceCalculationResult(200000m, 20000m, baseShippingFee, 0m, baseShippingFee,
            180000m + baseShippingFee, []
        ));
        return pricing;
    }

    private static CheckoutRequest FromJson(string body) =>
        JsonSerializer.Deserialize<CheckoutRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task CheckoutAsync_APostedShippingFeeIsInert_AndTheDebitIsMatchingsQuote() {
        using var dbContext = GetInMemoryDbContext();
        var cartService = CartWithOneItem();
        var pricing = PricingPassingShippingThrough();
        var shipping = ShippingReturning(RateCardFloor());
        var escrow = AcceptingEscrow();

        var request = FromJson("""
            {
              "shippingAddress": "Jl. Sudirman No. 45, Jakarta",
              "voucherCode": null,
              "shippingServiceTier": "KINETIX_INSTANT",
              "baseShippingFee": 0,
              "distanceKm": 0
            }
            """);

        var orderService = NewOrderService(
            dbContext,
            cartService.Object,
            pricing.Object,
            shipping.Object,
            escrow.Object
        );

        var result = await orderService.CheckoutAsync(Customer, request, "IDEMP-KEY-12345");

        escrow.Verify(c => c.CreateHoldAsync(
            It.IsAny<string>(), Customer, Merchant, It.IsAny<string?>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), 15000m
        ), Times.Once);

        pricing.Verify(p => p.CalculatePriceAsync(
            It.IsAny<string?>(), It.IsAny<IReadOnlyList<PriceLine>>(), 15000m
        ), Times.Once);

        Assert.Equal(15000m, result.BaseShippingFee);
        Assert.Equal(15000m, result.FinalShippingFee);
        Assert.Equal("KINETIX_INSTANT", result.ShippingServiceTier);
        Assert.Equal("TIER_FLOOR", result.ShippingQuoteBasis);
    }

    [Fact]
    public async Task CheckoutAsync_RecordsTheQuotedDistance_NotOneTheCallerAsserted() {
        using var dbContext = GetInMemoryDbContext();
        var orderService = NewOrderService(dbContext, CartWithOneItem().Object,
            PricingPassingShippingThrough().Object, ShippingReturning(RateCardFloor()).Object);

        var request = FromJson("""
            {"shippingAddress":"Jl. Sudirman No. 45, Jakarta","distanceKm":5.2,"baseShippingFee":1}
            """);

        var result = await orderService.CheckoutAsync(Customer, request, "IDEMP-KEY-DISTANCE");

        Assert.Equal(0.0, result.DistanceKm);
    }

    [Fact]
    public async Task CheckoutAsync_SendsTheCartsMerchantPrincipalToMatching() {
        using var dbContext = GetInMemoryDbContext();
        var shipping = ShippingReturning(RateCardFloor());

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object,
            PricingPassingShippingThrough().Object, shipping.Object);

        await orderService.CheckoutAsync(Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-MERCHANT"
        );

        shipping.Verify(c => c.EstimateShippingOptionsAsync(
            It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<long>(), Merchant
        ), Times.Once);
    }

    [Fact]
    public async Task CheckoutAsync_AsksMatchingAQuestionThatCarriesNoLocationClaim() {
        using var dbContext = GetInMemoryDbContext();
        var shipping = ShippingReturning(RateCardFloor());

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            shipping.Object
        );

        await orderService.CheckoutAsync(Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-PROBE"
        );

        shipping.Verify(c => c.EstimateShippingOptionsAsync(
            ShippingRateCardProbe.Latitude, ShippingRateCardProbe.Longitude,
            ShippingRateCardProbe.Latitude, ShippingRateCardProbe.Longitude,
            ShippingRateCardProbe.WeightGrams, It.IsAny<string>()
        ), Times.Once);
    }

    [Fact]
    public async Task CheckoutAsync_WithNoTierRequested_TakesTheCheapestAvailableOption() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        var result = await orderService.CheckoutAsync(Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-DEFAULT"
        );

        Assert.Equal("KINETIX_REGULAR", result.ShippingServiceTier);
        Assert.Equal(9000m, result.BaseShippingFee);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), 9000m
        ), Times.Once);
    }

    [Fact]
    public async Task CheckoutAsync_WhenMatchingDoesNotAnswer_RefusesAndWritesNothing() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var shipping = new Mock<IShippingClient>();
        shipping.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<long>(), It.IsAny<string>()
        )).ThrowsAsync(new ShippingUnavailableException(new TimeoutException("deadline exceeded")));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            shipping.Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<ShippingUnavailableException>(() => orderService.CheckoutAsync(
            Customer, new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-DOWN"
        ));

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()
        ), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_WhenNoTierIsAvailable_RefusesRatherThanPricingAtZero() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var nothingServes = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("KINETIX_INSTANT", "Kinetix Express Instant", 800.0, 2415000m,
                "1 - 2 Jam", false, "Distance exceeds 15km limit"
            ),
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 800.0, 9000m,
                "1 - 3 Hari", false, "Distance exceeds 500km limit"
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(nothingServes).Object,
            escrow.Object
        );

        var refusal = await Assert.ThrowsAsync<ShippingNotServiceableException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null),
                "IDEMP-KEY-NOSERVICE"
            )
        );

        Assert.Contains("KINETIX_REGULAR: Distance exceeds 500km limit", refusal.Reasons);
        Assert.Empty(dbContext.Orders);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()
        ), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheChosenTierArrivesWithNoFee_RefusesRatherThanShippingFree() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var unstatedFee = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, null, "1 - 3 Hari", true, null),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(unstatedFee).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_REGULAR"),
                "IDEMP-KEY-NOFEE"
            )
        );

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()
        ), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheChosenTierIsQuotedAtZero_RefusesRatherThanShippingFree() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var freeQuote = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, 0m,
                "1 - 3 Hari", true, null),
        ]);

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object,
            PricingPassingShippingThrough().Object, ShippingReturning(freeQuote).Object, escrow.Object
        );

        await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_REGULAR"),
                "IDEMP-KEY-ZEROFEE"
            )
        );

        Assert.Empty(dbContext.Orders);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()
        ), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_WithNoTierRequested_WillNotPickACheapestItCannotEstablish() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var oneUnreadable = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, 9000m,
                "1 - 3 Hari", true, null
            ),
            new ShippingOptionResult("KINETIX_DRONE", "Kinetix Drone", 0.0, null,
                "1 Jam", true, null
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(oneUnreadable).Object,
            escrow.Object
        );

        var refusal = await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-CHEAPEST"
            )
        );

        Assert.Contains("KINETIX_DRONE", refusal.Fault);
        Assert.Empty(dbContext.Orders);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()
        ), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_WithATierRequested_IsUnaffectedByAnUnreadableOptionItDidNotAskFor() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var oneUnreadable = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, 9000m,
                "1 - 3 Hari", true, null),
            new ShippingOptionResult("KINETIX_DRONE", "Kinetix Drone", 0.0, null, "1 Jam", true, null),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(oneUnreadable).Object,
            escrow.Object
        );

        var result = await orderService.CheckoutAsync(Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_REGULAR"),
            "IDEMP-KEY-UNAFFECTED"
        );

        Assert.Equal(9000m, result.BaseShippingFee);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), 9000m
        ), Times.Once);
    }

    [Fact]
    public async Task CheckoutAsync_WhenMatchingListsNoOptionsAtAll_IsNotAnUnserviceableAddress() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(new EstimateShippingResult(0.0, [])
        ).Object, escrow.Object);

        await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-EMPTY"
            )
        );

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()
        ), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_WhenATierAppearsTwice_TakesTheCheaperRatherThanTheFirstListed() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var duplicated = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, 9000m,
                "1 - 3 Hari", true, null),
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, 7000m,
                "1 - 3 Hari", true, null),
        ]);

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object,
            PricingPassingShippingThrough().Object, ShippingReturning(duplicated).Object, escrow.Object
        );

        var result = await orderService.CheckoutAsync(Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_REGULAR"),
            "IDEMP-KEY-DUPLICATE"
        );

        Assert.Equal(7000m, result.BaseShippingFee);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), 7000m
        ), Times.Once);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheCartCarriesNoMerchant_RefusesRatherThanInventingAPayee() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var shipping = ShippingReturning(RateCardFloor());

        var cartService = new Mock<ICartService>();
        var cart = new CustomerCart(Customer);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "Sample Product",
            UnitPrice = 100000m,
            Quantity = 2,
            MerchantPrincipalId = null
        });
        cartService.Setup(s => s.GetCartAsync(Customer)).ReturnsAsync(cart);

        var orderService = NewOrderService(dbContext, cartService.Object,
            PricingPassingShippingThrough().Object, shipping.Object, escrow.Object
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-NOMERCHANT"
            )
        );

        Assert.Empty(dbContext.Orders);
        shipping.Verify(c => c.EstimateShippingOptionsAsync(
            It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<long>(), It.IsAny<string>())
        , Times.Never);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()
        ), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheRequestedTierIsUnavailable_RefusesWithoutRepeatingTheRateCardAsAFact() {
        using var dbContext = GetInMemoryDbContext();

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object,
            PricingPassingShippingThrough().Object, ShippingReturning(RateCardFloor()).Object);

        var refusal = await Assert.ThrowsAsync<ShippingTierNotEstablishedException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_CARGO"),
                "IDEMP-KEY-CARGO"));

        Assert.Equal("KINETIX_CARGO", refusal.RequestedTier);
        Assert.Equal("Cargo is reserved for packages >= 10kg", refusal.RateCardReason);
        Assert.Contains("KINETIX_REGULAR", refusal.AvailableTiers);

        Assert.Contains("could not be established", refusal.Message);
        Assert.Contains("0 km and 0 kg", refusal.Message);
        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheRequestedTierIsUnknown_RefusesAsAnUnknownTier() {
        using var dbContext = GetInMemoryDbContext();

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object,
            PricingPassingShippingThrough().Object, ShippingReturning(RateCardFloor()).Object);

        var refusal = await Assert.ThrowsAsync<ShippingTierUnknownException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_TELEPORT"),
                "IDEMP-KEY-TELEPORT"));

        Assert.Equal("KINETIX_TELEPORT", refusal.RequestedTier);
        Assert.Contains("KINETIX_REGULAR", refusal.AvailableTiers);
        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_MatchesTheTierOrdinally() {
        using var dbContext = GetInMemoryDbContext();

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object,
            PricingPassingShippingThrough().Object, ShippingReturning(RateCardFloor()).Object);

        await Assert.ThrowsAsync<ShippingTierUnknownException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "kinetix_instant"),
                "IDEMP-KEY-CASE"));
    }

    [Fact]
    public async Task CheckoutAsync_WhenPricingContradictsTheQuotedBase_RefusesBeforeCharging() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var pricing = new Mock<IPricingClient>();
        pricing.Setup(p => p.CalculatePriceAsync(
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<PriceLine>>(), It.IsAny<decimal>()))
            .ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 0m, 0m, 0m, 180000m, []));

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object, pricing.Object,
            ShippingReturning(RateCardFloor()).Object, escrow.Object);

        var refusal = await Assert.ThrowsAsync<ShippingFeeContradictedException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-CONTRADICT"));

        Assert.Equal(9000m, refusal.QuotedBase);
        Assert.Empty(dbContext.Orders);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_WhenPricingDiscountsBelowZero_RefusesBeforeCharging() {
        using var dbContext = GetInMemoryDbContext();

        var pricing = new Mock<IPricingClient>();
        pricing.Setup(p => p.CalculatePriceAsync(
                It.IsAny<string?>(), It.IsAny<IReadOnlyList<PriceLine>>(), It.IsAny<decimal>()))
            .ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 9000m, 12000m, -3000m, 177000m, []));

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object, pricing.Object,
            ShippingReturning(RateCardFloor()).Object);

        await Assert.ThrowsAsync<ShippingFeeContradictedException>(() =>
            orderService.CheckoutAsync(Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null), "IDEMP-KEY-NEGATIVE"));

        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_AShippingDiscountStillReducesTheDebit() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var pricing = new Mock<IPricingClient>();
        pricing.Setup(p => p.CalculatePriceAsync(
                "FREE_SHIP", It.IsAny<IReadOnlyList<PriceLine>>(), 9000m))
            .ReturnsAsync(new PriceCalculationResult(200000m, 0m, 9000m, 9000m, 0m, 200000m, []));

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object, pricing.Object,
            ShippingReturning(RateCardFloor()).Object, escrow.Object);

        var result = await orderService.CheckoutAsync(Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", "FREE_SHIP"), "IDEMP-KEY-FREESHIP");

        Assert.Equal(9000m, result.BaseShippingFee);
        Assert.Equal(0m, result.FinalShippingFee);
        escrow.Verify(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), 0m), Times.Once);
    }

    [Fact]
    public async Task CheckoutAsync_CreatesOrderAndClearsCart() {
        using var dbContext = GetInMemoryDbContext();
        var cartService = CartWithOneItem();
        var pricing = new Mock<IPricingClient>();
        var shipping = ShippingReturning(RateCardFloor());

        pricing.Setup(p => p.CalculatePriceAsync("DISCOUNT10", It.IsAny<IReadOnlyList<PriceLine>>(), 15000m))
            .ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 15000m, 0m, 15000m, 195000m, []));

        var orderService = NewOrderService(dbContext, cartService.Object, pricing.Object, shipping.Object);
        var request = new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", "DISCOUNT10", "KINETIX_INSTANT");

        var result = await orderService.CheckoutAsync(Customer, request, "IDEMP-KEY-12345");

        Assert.NotNull(result);
        Assert.StartsWith("ORD-", result.OrderNumber);
        Assert.Equal("PENDING_PAYMENT", result.Status);
        Assert.Equal(200000m, result.Subtotal);
        Assert.Equal(20000m, result.DiscountAmount);
        Assert.Equal(15000m, result.BaseShippingFee);
        Assert.Equal(195000m, result.FinalTotal);
        Assert.Equal("KINETIX_INSTANT", result.ShippingServiceTier);
        Assert.Equal(0.0, result.DistanceKm);

        cartService.Verify(s => s.ClearCartAsync(Customer), Times.Once);
        pricing.Verify(p => p.CalculatePriceAsync("DISCOUNT10", It.IsAny<IReadOnlyList<PriceLine>>(), 15000m), Times.Once);
    }

    [Fact]
    public async Task TransitionOrderStatusAsync_ValidTransition_UpdatesStatus() {
        using var dbContext = GetInMemoryDbContext();
        var mockCartService = new Mock<ICartService>();
        var mockPricingClient = new Mock<IPricingClient>();

        var order = new OrderEntity {
            OrderNumber = "ORD-20260815-001",
            CustomerPrincipalId = Customer,
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = 100000m,
            FinalTotal = 100000m,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta"
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();

        var orderService = NewOrderService(dbContext, mockCartService.Object, mockPricingClient.Object,
            ShippingReturning(RateCardFloor()).Object);

        var result = await orderService.TransitionOrderStatusAsync(order.Id, OrderStatus.PAID);

        Assert.Equal("PAID", result.Status);
    }

    [Fact]
    public async Task TransitionOrderStatusAsync_InvalidTransition_ThrowsException() {
        using var dbContext = GetInMemoryDbContext();
        var mockCartService = new Mock<ICartService>();
        var mockPricingClient = new Mock<IPricingClient>();

        var order = new OrderEntity {
            OrderNumber = "ORD-20260815-002",
            CustomerPrincipalId = Customer,
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = 100000m,
            FinalTotal = 100000m,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta"
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();

        var orderService = NewOrderService(dbContext, mockCartService.Object, mockPricingClient.Object,
            ShippingReturning(RateCardFloor()).Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orderService.TransitionOrderStatusAsync(order.Id, OrderStatus.DELIVERED));
    }

    [Fact]
    public async Task LegacyRowsReadBackAsClientSupplied() {
        using var dbContext = GetInMemoryDbContext();

        var order = new OrderEntity {
            OrderNumber = "ORD-20260815-003",
            CustomerPrincipalId = Customer,
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = 100000m,
            FinalTotal = 100000m,
            BaseShippingFee = 0m,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta"
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();

        var orderService = NewOrderService(dbContext, new Mock<ICartService>().Object,
            new Mock<IPricingClient>().Object, ShippingReturning(RateCardFloor()).Object);

        var result = await orderService.GetOrderByIdAsync(order.Id);

        Assert.Equal("CLIENT_SUPPLIED", result!.ShippingQuoteBasis);
    }
}
