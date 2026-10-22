using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Products;
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
    private const string Buyer = "Test Buyer";
    private const string BuyerPhone = "081200000000";

    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static EstimateShippingResult RateCardFloor() => new(0.0, [
        new ShippingOptionResult("KINETIX_INSTANT", "Kinetix Express Instant", 0.0, "1 - 2 Jam", true, null),
        new ShippingOptionResult("KINETIX_SAMEDAY", "Kinetix SameDay", 0.0, "6 - 8 Jam", true, null),
        new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, "1 - 3 Hari", true, null),
        new ShippingOptionResult(
            "KINETIX_CARGO",
            "Kinetix Cargo Heavy",
            0.0,
            "3 - 5 Hari",
            false,
            "Cargo is reserved for packages >= 10kg"
        ),
    ]);

    private static readonly MapPoint Store = new(-6.1754, 106.8272);

    private static readonly MapPoint Home = new(-6.2088, 106.8456);

    private static Mock<IAddressDirectory> StoreOnTheMap() {
        var addresses = new Mock<IAddressDirectory>();
        addresses.Setup(a => a.PickupPointAsync(It.IsAny<string>())).ReturnsAsync(Store);
        return addresses;
    }

    private static Mock<IAddressGeocoder> GeocoderPlacing(AddressPlacement placement) {
        var geocoder = new Mock<IAddressGeocoder>();
        geocoder.Setup(g => g.PlaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(placement);
        return geocoder;
    }

    private static readonly Dictionary<string, decimal> RateCard = new(StringComparer.Ordinal) {
        ["KINETIX_INSTANT"] = 15000m,
        ["KINETIX_SAMEDAY"] = 12000m,
        ["KINETIX_REGULAR"] = 9000m,
        ["KINETIX_CARGO"] = 25000m,
    };

    private static Mock<IPricingClient> PricingWithRatesFor(params string[] tiers) {
        var known = tiers.ToHashSet(StringComparer.Ordinal);
        var pricing = new Mock<IPricingClient>();

        pricing.Setup(p => p.QuoteShippingAsync(It.IsAny<IReadOnlyList<ShippingJourney>>()))
            .ReturnsAsync((IReadOnlyList<ShippingJourney> journeys) =>
                [.. journeys.Select(j => new QuotedShippingResult(
                    j.ServiceTier,
                    known.Contains(j.ServiceTier) ? RateCard.GetValueOrDefault(j.ServiceTier) : 0m,
                    known.Contains(j.ServiceTier)
                ))]
            );

        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync((string? _, IReadOnlyList<PriceLine> _, ShippingJourney? shipping, string _) => {
            decimal fee = shipping is null ? 0m : RateCard.GetValueOrDefault(shipping.ServiceTier);

            return new PriceCalculationResult(200000m, 20000m, fee, 0m, fee, 180000m + fee, TwoOfProductOne);
        });

        return pricing;
    }

    private static void QuotingFromTheRateCard(Mock<IPricingClient> pricing) =>
        pricing.Setup(p => p.QuoteShippingAsync(It.IsAny<IReadOnlyList<ShippingJourney>>()))
            .ReturnsAsync((IReadOnlyList<ShippingJourney> journeys) =>
                [.. journeys.Select(j => new QuotedShippingResult(
                    j.ServiceTier,
                    RateCard.TryGetValue(j.ServiceTier, out var fee) ? fee : 0m,
                    RateCard.ContainsKey(j.ServiceTier)
                ))]
            );

    private static Mock<IShippingClient> ShippingReturning(EstimateShippingResult result) {
        var shipping = new Mock<IShippingClient>();
        shipping.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<long>(),
            It.IsAny<string>()
        )).ReturnsAsync(result);
        return shipping;
    }

    private static Mock<IEscrowClient> AcceptingEscrow() {
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(c => c.CreateHoldAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<decimal>(),
            It.IsAny<decimal>(),
            It.IsAny<decimal>()
        )).ReturnsAsync(StepResult.Ok());
        return escrow;
    }

    private static OrderApplicationService NewOrderService(
        OrderDbContext db,
        ICartService cart,
        IPricingClient pricing,
        IShippingClient shipping,
        IEscrowClient? escrowClient = null,
        IProductDirectory? catalog = null,
        IVoucherQuotaClient? voucherClient = null,
        IMerchantStanding? merchants = null,
        IAddressDirectory? addresses = null,
        IAddressGeocoder? geocoder = null
    ) {
        var voucher = new Mock<IVoucherQuotaClient>();
        voucher.Setup(c => c.RedeemVoucherAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        ))
            .ReturnsAsync(StepResult.Ok());

        var flash = new Mock<IFlashSaleClient>();
        flash.Setup(c => c.AllocateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var stock = new Mock<IStockClient>();
        stock.Setup(c => c.ReserveStockAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>()
        ))
            .ReturnsAsync(StepResult.Ok());

        var escrow = escrowClient ?? AcceptingEscrow().Object;

        var policy = new CompensationPolicy();
        var fulfilment = new Mock<IFulfillmentClient>();
        fulfilment.Setup(c => c.CreateTaskAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<FulfillmentLine>>()
        ))
            .ReturnsAsync(new FulfillmentCreated(StepResult.Ok(), "901"));
        fulfilment.Setup(c => c.CancelTaskAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var runner = new CheckoutSagaRunner(
            db,
            voucherClient ?? voucher.Object,
            flash.Object,
            stock.Object,
            escrow,
            fulfilment.Object,
            new FakeSagaLeaseStore(db, policy),
            policy,
            new RequestIdAccessor(new HttpContextAccessor()),
            NullLogger<CheckoutSagaRunner>.Instance
        );

        return new OrderApplicationService(
            db,
            cart,
            catalog ?? CatalogAnswering().Object,
            merchants ?? MerchantsThatMaySell().Object,
            pricing,
            shipping,
            addresses ?? StoreOnTheMap().Object,
            geocoder ?? GeocoderPlacing(AddressPlacement.Placed(Home)).Object,
            runner,
            NullLogger<OrderApplicationService>.Instance
        );
    }

    private static Mock<IMerchantStanding> MerchantsThatMaySell() {
        var merchants = new Mock<IMerchantStanding>();
        merchants.Setup(m => m.MaySellAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return merchants;
    }

    private static OrderDbContext GetInMemoryDbContext() {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new OrderDbContext(options);
    }

    private static Mock<IProductDirectory> CatalogAnswering() {
        var catalog = new Mock<IProductDirectory>();
        catalog.Setup(c => c.GetProductAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sku, CancellationToken _) =>
                new CatalogProduct(sku, Merchant, "Sample Product", 100000m, null, 500));
        return catalog;
    }

    private static Mock<IProductDirectory> CatalogNaming(params (string Sku, string Merchant)[] products) {
        var catalog = new Mock<IProductDirectory>();
        foreach (var (sku, merchant) in products) {
            var answer = new CatalogProduct(sku, merchant, sku, 100000m, null, 500);
            catalog.Setup(c => c.GetProductAsync(sku, It.IsAny<CancellationToken>())).ReturnsAsync(answer);
        }

        return catalog;
    }

    private static readonly IReadOnlyList<PricedLine> TwoOfProductOne = [
        new PricedLine("PRODUCT-01", 2, null, 100000m, 200000m),
    ];

    private static IReadOnlyList<PricedLine> EachLineAtItsOwnPrice(IReadOnlyList<PriceLine> lines) =>
        [.. lines.Select(l => new PricedLine(l.ProductId, l.Quantity, null, l.UnitPrice, l.UnitPrice * l.Quantity))];

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
        QuotingFromTheRateCard(pricing);
        QuotingFromTheRateCard(pricing);

        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync((string? _, IReadOnlyList<PriceLine> _, ShippingJourney? shipping, string _) => {
            decimal baseShippingFee = shipping is null
                ? 0m
                : RateCard.GetValueOrDefault(shipping.ServiceTier);

            return new PriceCalculationResult(
                200000m,
                20000m,
                baseShippingFee,
                0m,
                baseShippingFee,
                180000m + baseShippingFee,
                TwoOfProductOne
            );
        });
        return pricing;
    }

    private static Mock<IPricingClient> PricingAtCatalogPrices() {
        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);

        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync((string? _, IReadOnlyList<PriceLine> lines, ShippingJourney? journey, string _) => {
            decimal subtotal = lines.Sum(l => l.UnitPrice * l.Quantity);
            decimal fee = journey is null ? 0m : RateCard.GetValueOrDefault(journey.ServiceTier);

            return new PriceCalculationResult(
                subtotal,
                0m,
                fee,
                0m,
                fee,
                subtotal + fee,
                EachLineAtItsOwnPrice(lines)
            );
        });
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
              "recipientName": "Test Buyer",
              "recipientPhone": "081200000000",
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

        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                Customer,
                Merchant,
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                15000m
            ),
            Times.Once
        );

        pricing.Verify(
            p => p.CalculatePriceAsync(
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<PriceLine>>(),
                It.Is<ShippingJourney?>(j => j!.ServiceTier == "KINETIX_INSTANT"),
                It.IsAny<string>()
            ),
            Times.Once
        );

        Assert.Equal(15000m, result.BaseShippingFee);
        Assert.Equal(15000m, result.FinalShippingFee);
        Assert.Equal("KINETIX_INSTANT", result.ShippingServiceTier);
        Assert.Equal("DISTANCE_QUOTED", result.ShippingQuoteBasis);
    }

    [Fact]
    public async Task CheckoutAsync_RecordsTheQuotedDistance_NotOneTheCallerAsserted() {
        using var dbContext = GetInMemoryDbContext();
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var request = FromJson("""
            {
              "recipientName": "Test Buyer",
              "recipientPhone": "081200000000",
              "shippingAddress": "Jl. Sudirman No. 45, Jakarta",
              "distanceKm": 5.2,
              "baseShippingFee": 1
            }
            """);

        var result = await orderService.CheckoutAsync(Customer, request, "IDEMP-KEY-DISTANCE");

        Assert.Equal(0.0, result.DistanceKm);
    }

    [Fact]
    public async Task CheckoutAsync_SendsTheCartsMerchantPrincipalToMatching() {
        using var dbContext = GetInMemoryDbContext();
        var shipping = ShippingReturning(RateCardFloor());

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            shipping.Object
        );

        await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-MERCHANT"
        );

        shipping.Verify(
            c => c.EstimateShippingOptionsAsync(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<long>(),
                Merchant
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task CheckoutAsync_AsksMatchingAboutTheStoreToTheGeocodedAddress_ForTheWeighedParcel() {
        using var dbContext = GetInMemoryDbContext();
        var shipping = ShippingReturning(RateCardFloor());
        var pricing = PricingPassingShippingThrough();

        var orderService = NewOrderService(dbContext, CartWithOneItem().Object, pricing.Object, shipping.Object);

        await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-ROUTE"
        );

        shipping.Verify(
            c => c.EstimateShippingOptionsAsync(
                Store.Latitude,
                Store.Longitude,
                Home.Latitude,
                Home.Longitude,
                1000L,
                Merchant
            ),
            Times.Once
        );
        pricing.Verify(
            p => p.QuoteShippingAsync(
                It.Is<IReadOnlyList<ShippingJourney>>(journeys => journeys.All(j => j.TotalWeightGrams == 1000L))
            ),
            Times.Once
        );

        var order = Assert.Single(dbContext.Orders);
        Assert.Equal(Home.Latitude, order.DeliveryLatitude);
        Assert.Equal(Home.Longitude, order.DeliveryLongitude);
        Assert.Equal(1000L, order.ParcelWeightGrams);
    }

    [Fact]
    public async Task CheckoutAsync_WeighsEveryLine_ByItsOwnWeightAndQuantity() {
        using var dbContext = GetInMemoryDbContext();
        var shipping = ShippingReturning(RateCardFloor());
        var cartService = new Mock<ICartService>();
        var cart = new CustomerCart(Customer);
        cart.Items.Add(new CartItem {
            ProductId = "LIGHT-1",
            ProductTitle = "Light",
            UnitPrice = 100000m,
            Quantity = 1,
            MerchantPrincipalId = Merchant
        });
        cart.Items.Add(new CartItem {
            ProductId = "HEAVY-3",
            ProductTitle = "Heavy",
            UnitPrice = 100000m,
            Quantity = 3,
            MerchantPrincipalId = Merchant
        });
        cartService.Setup(c => c.GetCartAsync(Customer)).ReturnsAsync(cart);

        var catalog = new Mock<IProductDirectory>();
        catalog.Setup(c => c.GetProductAsync("LIGHT-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogProduct("LIGHT-1", Merchant, "Light", 100000m, null, 250));
        catalog.Setup(c => c.GetProductAsync("HEAVY-3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogProduct("HEAVY-3", Merchant, "Heavy", 100000m, null, 1200));

        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync((string? _, IReadOnlyList<PriceLine> lines, ShippingJourney? journey, string _) => {
            decimal fee = journey is null ? 0m : RateCard.GetValueOrDefault(journey.ServiceTier);
            decimal subtotal = lines.Sum(l => l.UnitPrice * l.Quantity);
            return new PriceCalculationResult(subtotal, 0m, fee, 0m, fee, subtotal + fee, EachLineAtItsOwnPrice(lines));
        });

        var orderService = NewOrderService(
            dbContext,
            cartService.Object,
            pricing.Object,
            shipping.Object,
            catalog: catalog.Object
        );

        await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-WEIGHTS"
        );

        shipping.Verify(
            c => c.EstimateShippingOptionsAsync(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                3850L,
                Merchant
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task CheckoutAsync_RefusesAProductWithNoWeight_BeforeAskingForAQuote() {
        using var dbContext = GetInMemoryDbContext();
        var shipping = ShippingReturning(RateCardFloor());
        var catalog = new Mock<IProductDirectory>();
        catalog.Setup(c => c.GetProductAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sku, CancellationToken _) =>
                new CatalogProduct(sku, Merchant, "Unweighed", 100000m, null, null)
            );

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            shipping.Object,
            catalog: catalog.Object
        );

        var refusal = await Assert.ThrowsAsync<ProductWeightUnknownException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-UNWEIGHED"
            )
        );

        Assert.Equal(["PRODUCT-01"], refusal.ProductIds);
        Assert.Empty(dbContext.Orders);
        shipping.Verify(
            c => c.EstimateShippingOptionsAsync(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<long>(),
                It.IsAny<string>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_RefusesWhenTheMerchantsStoreHasNoPointOnTheMap() {
        using var dbContext = GetInMemoryDbContext();
        var unplaced = new Mock<IAddressDirectory>();
        unplaced.Setup(a => a.PickupPointAsync(It.IsAny<string>())).ReturnsAsync((MapPoint?)null);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object,
            addresses: unplaced.Object
        );

        await Assert.ThrowsAsync<MerchantStoreUnplacedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-NOSTORE"
            )
        );

        Assert.Empty(dbContext.Orders);
    }

    [Theory]
    [InlineData(AddressPlacementFailure.NotFound)]
    [InlineData(AddressPlacementFailure.Ambiguous)]
    [InlineData(AddressPlacementFailure.OutOfServiceArea)]
    [InlineData(AddressPlacementFailure.Insufficient)]
    [InlineData(AddressPlacementFailure.TooVague)]
    public async Task CheckoutAsync_RefusesAnAddressIdentityCannotPlace_SayingWhy(AddressPlacementFailure failure) {
        using var dbContext = GetInMemoryDbContext();
        var shipping = ShippingReturning(RateCardFloor());

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            shipping.Object,
            geocoder: GeocoderPlacing(AddressPlacement.Refused(failure, "identity's reason")).Object
        );

        var refusal = await Assert.ThrowsAsync<ShippingAddressUnplacedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Somewhere", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-UNPLACED"
            )
        );

        Assert.Equal(failure, refusal.Failure);
        Assert.Empty(dbContext.Orders);
        shipping.Verify(
            c => c.EstimateShippingOptionsAsync(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<long>(),
                It.IsAny<string>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheAddressCannotBeLookedUp_SaysSoRatherThanBlamingTheAddress() {
        using var dbContext = GetInMemoryDbContext();
        var geocoder = new Mock<IAddressGeocoder>();
        geocoder.Setup(g => g.PlaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AddressPlacementUnavailableException("the geocoder is down"));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object,
            geocoder: geocoder.Object
        );

        await Assert.ThrowsAsync<AddressPlacementUnavailableException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-GEODOWN"
            )
        );

        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_RefusesABlankShippingAddress_BeforeAnythingIsAsked() {
        using var dbContext = GetInMemoryDbContext();
        var geocoder = GeocoderPlacing(AddressPlacement.Placed(Home));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object,
            geocoder: geocoder.Object
        );

        var refusal = await Assert.ThrowsAsync<RecipientMissingException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("  ", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-BLANK"
            )
        );

        Assert.Equal("shippingAddress", refusal.Field);
        geocoder.Verify(g => g.PlaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var result = await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-DEFAULT"
        );

        Assert.Equal("KINETIX_REGULAR", result.ShippingServiceTier);
        Assert.Equal(9000m, result.BaseShippingFee);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                9000m
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenMatchingDoesNotAnswer_RefusesAndWritesNothing() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var shipping = new Mock<IShippingClient>();
        shipping.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<long>(),
            It.IsAny<string>()
        )).ThrowsAsync(new ShippingUnavailableException(new TimeoutException("deadline exceeded")));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            shipping.Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<ShippingUnavailableException>(() => orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-DOWN"
        ));

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenEveryTierComesBackUnavailable_IsRefusedAsNotServiceable() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var nothingServes = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                "KINETIX_INSTANT",
                "Kinetix Express Instant",
                800.0,
                "1 - 2 Jam",
                false,
                "Distance exceeds 15km limit"
            ),
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                800.0,
                "1 - 3 Hari",
                false,
                "Distance exceeds 500km limit"
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
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-NOSERVICE"
            )
        );

        Assert.Contains("KINETIX_REGULAR: Distance exceeds 500km limit", refusal.Reasons);
        Assert.Empty(dbContext.Orders);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheChosenTierArrivesWithNoFee_RefusesRatherThanShippingFree() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var offered = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 0.0, "1 - 3 Hari", true, null),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingWithRatesFor().Object,
            ShippingReturning(offered).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<ShippingTierNotEstablishedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_REGULAR", Buyer, BuyerPhone),
                "IDEMP-KEY-NOFEE"
            )
        );

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheChosenTierIsQuotedAtZero_RefusesRatherThanShippingFree() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var offered = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingWithRatesFor().Object,
            ShippingReturning(offered).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<ShippingTierNotEstablishedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-ZEROFEE"
            )
        );

        Assert.Empty(dbContext.Orders);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WithNoTierRequested_WillNotPickACheapestItCannotEstablish() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var droneIsUnpriced = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
            new ShippingOptionResult(
                "KINETIX_DRONE",
                "Kinetix Drone",
                0.0,
                "1 Jam",
                true,
                null
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingWithRatesFor("KINETIX_REGULAR").Object,
            ShippingReturning(droneIsUnpriced).Object,
            escrow.Object
        );

        var result = await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-CHEAPEST"
        );

        Assert.Equal("KINETIX_REGULAR", result.ShippingServiceTier);
        Assert.Equal(9000m, result.BaseShippingFee);
        Assert.Single(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_WithATierRequested_RefusesAnUnreadableOptionItDidNotAskFor() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var droneIsUnpriced = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
            new ShippingOptionResult("KINETIX_DRONE", "Kinetix Drone", 0.0, "1 Jam", true, null),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingWithRatesFor("KINETIX_REGULAR").Object,
            ShippingReturning(droneIsUnpriced).Object,
            escrow.Object
        );

        var result = await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_REGULAR", Buyer, BuyerPhone),
            "IDEMP-KEY-UNAFFECTED"
        );

        Assert.Equal("KINETIX_REGULAR", result.ShippingServiceTier);
        Assert.Equal(9000m, result.BaseShippingFee);
        Assert.Single(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_BothSelectionPathsCallAnUnreadableOptionTheSameThing() {
        var nothingIsPriced = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("KINETIX_DRONE", "Kinetix Drone", 0.0, "1 Jam", true, null),
        ]);

        async Task<Exception?> Outcome(string? requestedTier) {
            using var dbContext = GetInMemoryDbContext();
            var orderService = NewOrderService(
                dbContext,
                CartWithOneItem().Object,
                PricingWithRatesFor().Object,
                ShippingReturning(nothingIsPriced).Object
            );

            return await Record.ExceptionAsync(() => orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, requestedTier, Buyer, BuyerPhone),
                $"IDEMP-KEY-DOORS-{requestedTier ?? "NONE"}"
            ));
        }

        var cheapestPath = await Outcome(null);
        var namedPath = await Outcome("KINETIX_DRONE");

        Assert.NotNull(cheapestPath);
        Assert.NotNull(namedPath);
        Assert.Equal(cheapestPath.GetType(), namedPath.GetType());
        Assert.IsType<ShippingTierNotEstablishedException>(cheapestPath);
    }

    [Fact]
    public async Task CheckoutAsync_WhenMatchingListsNoOptionsAtAll_IsNotAnUnserviceableAddress() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(new EstimateShippingResult(0.0, [])
        ).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-EMPTY"
            )
        );

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenATierAppearsTwice_RefusesRatherThanPickingWhichFeeIsThePrice() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var duplicated = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(duplicated).Object,
            escrow.Object
        );

        var refusal = await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_REGULAR", Buyer, BuyerPhone),
                "IDEMP-KEY-DUPLICATE"
            )
        );

        Assert.Contains("KINETIX_REGULAR", refusal.Fault);
        Assert.Empty(dbContext.Orders);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenATierAppearsTwice_RefusesOnTheCheapestPathToo() {
        using var dbContext = GetInMemoryDbContext();

        var duplicated = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(duplicated).Object
        );

        await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-DUPLICATE-CHEAPEST"
            )
        );

        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_WhenAnOptionCarriesNoServiceTier_RefusesRatherThanSellingABlankTier() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var blankTier = new EstimateShippingResult(0.0, [
            new ShippingOptionResult("", "", 0.0, "", true, null),
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(blankTier).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-BLANKTIER"
            )
        );

        Assert.Empty(dbContext.Orders);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenAServiceTierIsLongerThanTheColumn_RefusesBeforeTheInsertCanFail() {
        using var dbContext = GetInMemoryDbContext();

        string overlongTier = new('K', OrderEntity.ServiceTierMaxLength + 1);

        var tooLong = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                overlongTier,
                "Kinetix Regular Freight",
                0.0,
                "1 - 3 Hari",
                true,
                null
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(tooLong).Object
        );

        await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-LONGTIER"
            )
        );

        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_WhenAnOptionIsAvailableAndSaysWhyItIsNot_RefusesTheContradiction() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var contradictory = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                "KINETIX_INSTANT",
                "Kinetix Express Instant",
                0.0,
                "1 - 2 Jam",
                true,
                "Distance exceeds 15km limit"
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(contradictory).Object,
            escrow.Object
        );

        var refusal = await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_INSTANT", Buyer, BuyerPhone),
                "IDEMP-KEY-CONTRADICTORY"
            )
        );

        Assert.Contains("KINETIX_INSTANT", refusal.Fault);
        Assert.Empty(dbContext.Orders);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_APriceWrittenIntoTheCartIsIgnored_CatalogsIsCharged() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var shipping = ShippingReturning(RateCardFloor());

        var cartService = new Mock<ICartService>();
        var cart = new CustomerCart(Customer);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "Free money, please",
            UnitPrice = 1m,
            Quantity = 2,
            CategoryId = "forged",
            MerchantPrincipalId = "MERCHANT-THE-BUYER-PICKED"
        });
        cartService.Setup(s => s.GetCartAsync(Customer)).ReturnsAsync(cart);

        var pricing = PricingPassingShippingThrough();
        IReadOnlyList<PriceLine> seen = [];
        string? pricedFor = null;
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).Callback((string? _, IReadOnlyList<PriceLine> lines, ShippingJourney? _, string merchant) => {
            seen = lines;
            pricedFor = merchant;
        })
          .ReturnsAsync((string? _, IReadOnlyList<PriceLine> lines, ShippingJourney? journey, string _) => {
              decimal subtotal = lines.Sum(l => l.UnitPrice * l.Quantity);
              decimal baseShippingFee = journey is null ? 0m : RateCard.GetValueOrDefault(journey.ServiceTier);
              return new PriceCalculationResult(
                  subtotal,
                  0m,
                  baseShippingFee,
                  0m,
                  baseShippingFee,
                  subtotal + baseShippingFee,
                  EachLineAtItsOwnPrice(lines)
              );
          });

        var orderService = NewOrderService(
            dbContext,
            cartService.Object,
            pricing.Object,
            shipping.Object,
            escrow.Object,
            CatalogNaming(("PRODUCT-01", Merchant)).Object
        );

        await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-FORGED-PRICE"
        );

        var line = Assert.Single(seen);
        Assert.Equal(100000m, line.UnitPrice);
        Assert.NotEqual(1m, line.UnitPrice);
        Assert.Equal(2, line.Quantity);

        Assert.NotEqual("forged", line.CategoryId);

        var order = Assert.Single(dbContext.Orders);
        Assert.Equal(Merchant, order.MerchantPrincipalId);
        Assert.Equal(200000m, order.Subtotal);
        Assert.Equal(Merchant, pricedFor);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheCartSpansTwoMerchants_RefusesRatherThanFilingItUnderTheFirst() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var shipping = ShippingReturning(RateCardFloor());

        var cartService = new Mock<ICartService>();
        var cart = new CustomerCart(Customer);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "From the first merchant",
            UnitPrice = 100000m,
            Quantity = 1,
            MerchantPrincipalId = "MERCHANT-ONE"
        });
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-02",
            ProductTitle = "From the second merchant",
            UnitPrice = 50000m,
            Quantity = 1,
            MerchantPrincipalId = "MERCHANT-TWO"
        });
        cartService.Setup(s => s.GetCartAsync(Customer)).ReturnsAsync(cart);

        var orderService = NewOrderService(
            dbContext,
            cartService.Object,
            PricingPassingShippingThrough().Object,
            shipping.Object,
            escrow.Object,
            CatalogNaming(("PRODUCT-01", "MERCHANT-ONE"), ("PRODUCT-02", "MERCHANT-TWO")).Object
        );

        var refusal = await Assert.ThrowsAsync<CartSpansTwoMerchantsException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-TWO-MERCHANTS"
            )
        );

        Assert.Equal(2, refusal.MerchantCount);

        Assert.DoesNotContain("MERCHANT-ONE", refusal.Message);
        Assert.DoesNotContain("MERCHANT-TWO", refusal.Message);

        Assert.Empty(dbContext.Orders);
        shipping.Verify(
            c => c.EstimateShippingOptionsAsync(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<long>(),
                It.IsAny<string>()
            ),
            Times.Never
        );
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenOneItemOfManyCarriesNoMerchant_RefusesRatherThanReadingPastIt() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var shipping = ShippingReturning(RateCardFloor());

        var cartService = new Mock<ICartService>();
        var cart = new CustomerCart(Customer);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "Carries a merchant",
            UnitPrice = 100000m,
            Quantity = 1,
            MerchantPrincipalId = "MERCHANT-ONE"
        });
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-02",
            ProductTitle = "Carries none",
            UnitPrice = 50000m,
            Quantity = 1,
            MerchantPrincipalId = null
        });
        cartService.Setup(s => s.GetCartAsync(Customer)).ReturnsAsync(cart);

        var orderService = NewOrderService(
            dbContext,
            cartService.Object,
            PricingPassingShippingThrough().Object,
            shipping.Object,
            escrow.Object,
            CatalogNaming(("PRODUCT-01", "MERCHANT-ONE"), ("PRODUCT-02", "")).Object
        );

        var refusal = await Assert.ThrowsAsync<ProductHasNoMerchantException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-ONE-MERCHANTLESS"
            )
        );

        Assert.Equal("PRODUCT-02", refusal.ProductId);

        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_LogsThePlacementAndThePaymentAsTheBuyers() {
        using var dbContext = GetInMemoryDbContext();
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingAtCatalogPrices().Object,
            ShippingReturning(RateCardFloor()).Object,
            AcceptingEscrow().Object
        );

        await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-LIFECYCLE"
        );

        var steps = await dbContext.OrderLifecycleEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync();
        Assert.Equal(
            [
                ((OrderStatus?)null, (OrderStatus?)OrderStatus.PENDING_PAYMENT),
                (OrderStatus.PENDING_PAYMENT, OrderStatus.PAID),
            ],
            steps.Select(e => (e.FromStatus, e.ToStatus))
        );
        Assert.All(steps, e => Assert.Equal(LifecycleActorKind.BUYER, e.ActorKind));
        Assert.All(steps, e => Assert.Equal(Customer, e.ActorPrincipalId));
        Assert.All(steps, e => Assert.Equal(Customer, e.BuyerPrincipalId));
    }

    [Fact]
    public async Task CheckoutAsync_WhenEveryItemSharesOneMerchant_ProceedsAsBefore() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var shipping = ShippingReturning(RateCardFloor());

        var cartService = new Mock<ICartService>();
        var cart = new CustomerCart(Customer);
        foreach (var sku in new[] { "PRODUCT-01", "PRODUCT-02", "PRODUCT-03" }) {
            cart.Items.Add(new CartItem {
                ProductId = sku,
                ProductTitle = sku,
                UnitPrice = 10000m,
                Quantity = 1,
                MerchantPrincipalId = "MERCHANT-ONE"
            });
        }

        cartService.Setup(s => s.GetCartAsync(Customer)).ReturnsAsync(cart);

        var orderService = NewOrderService(
            dbContext,
            cartService.Object,
            PricingAtCatalogPrices().Object,
            shipping.Object,
            escrow.Object
        );

        var order = await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-ONE-MERCHANT-MANY-ITEMS"
        );

        Assert.NotNull(order);
        Assert.Single(dbContext.Orders);
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

        var orderService = NewOrderService(
            dbContext,
            cartService.Object,
            PricingPassingShippingThrough().Object,
            shipping.Object,
            escrow.Object,
            CatalogNaming(("PRODUCT-01", "")).Object
        );

        await Assert.ThrowsAsync<ProductHasNoMerchantException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-NOMERCHANT"
            )
        );

        Assert.Empty(dbContext.Orders);
        shipping.Verify(
            c => c.EstimateShippingOptionsAsync(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<long>(),
                It.IsAny<string>()
            ),
            Times.Never
        );
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheRequestedTierIsUnavailable_RefusesWithTheFleetsReasonForThisParcel() {
        using var dbContext = GetInMemoryDbContext();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var refusal = await Assert.ThrowsAsync<ShippingTierNotEstablishedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_CARGO", Buyer, BuyerPhone),
                "IDEMP-KEY-CARGO"
            ));

        Assert.Equal("KINETIX_CARGO", refusal.RequestedTier);
        Assert.Equal("Cargo is reserved for packages >= 10kg", refusal.RateCardReason);
        Assert.Contains("KINETIX_REGULAR", refusal.AvailableTiers);

        Assert.Contains("could not be established for this parcel on this route", refusal.Message);
        Assert.Contains("Cargo is reserved for packages >= 10kg", refusal.Message);
        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheRequestedTierIsUnknown_RefusesAsAnUnknownTier() {
        using var dbContext = GetInMemoryDbContext();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var refusal = await Assert.ThrowsAsync<ShippingTierUnknownException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_TELEPORT", Buyer, BuyerPhone),
                "IDEMP-KEY-TELEPORT"
            ));

        Assert.Equal("KINETIX_TELEPORT", refusal.RequestedTier);
        Assert.Contains("KINETIX_REGULAR", refusal.AvailableTiers);
        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_MatchesTheTierOrdinally() {
        using var dbContext = GetInMemoryDbContext();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        await Assert.ThrowsAsync<ShippingTierUnknownException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "kinetix_instant", Buyer, BuyerPhone),
                "IDEMP-KEY-CASE"
            ));
    }

    [Fact]
    public async Task CheckoutAsync_WhenPricingContradictsTheQuotedBase_RefusesBeforeCharging() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        ))
            .ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 9000m, 0m, 12000m, 192000m, TwoOfProductOne));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        var refusal = await Assert.ThrowsAsync<ShippingFeeContradictedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-CONTRADICT"
            ));

        Assert.Equal(12000m, refusal.PricingFinal);
        Assert.Empty(dbContext.Orders);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenPricingDiscountsBelowZero_RefusesBeforeCharging() {
        using var dbContext = GetInMemoryDbContext();

        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        ))
            .ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 9000m, 12000m, -3000m, 177000m, TwoOfProductOne));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object
        );

        await Assert.ThrowsAsync<ShippingFeeContradictedException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-NEGATIVE"
            ));

        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_AShippingDiscountStillReducesTheDebit() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            "FREE_SHIP",
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.Is<ShippingJourney?>(j => j!.ServiceTier == "KINETIX_REGULAR"),
            It.IsAny<string>()
        ))
            .ReturnsAsync(new PriceCalculationResult(200000m, 0m, 9000m, 9000m, 0m, 200000m, TwoOfProductOne));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        var result = await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", "FREE_SHIP", null, Buyer, BuyerPhone),
            "IDEMP-KEY-FREESHIP"
        );

        Assert.Equal(9000m, result.BaseShippingFee);
        Assert.Equal(0m, result.FinalShippingFee);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                0m
            ),
            Times.Once
        );
    }

    [Theory]
    [InlineData(null, "081200000000", "recipientName")]
    [InlineData("", "081200000000", "recipientName")]
    [InlineData("   ", "081200000000", "recipientName")]
    [InlineData("Test Buyer", null, "recipientPhone")]
    [InlineData("Test Buyer", "  ", "recipientPhone")]
    public async Task CheckoutAsync_RefusesAnOrderWithNobodyToDeliverTo(
        string? name,
        string? phone,
        string expectedField
    ) {
        using var dbContext = GetInMemoryDbContext();
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var request = new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, name, phone);

        var thrown = await Assert.ThrowsAsync<RecipientMissingException>(
            () => orderService.CheckoutAsync(Customer, request, "IDEMP-KEY-NO-RECIPIENT")
        );
        Assert.Equal(expectedField, thrown.Field);
        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_CreatesOrderAndClearsCart() {
        using var dbContext = GetInMemoryDbContext();
        var cartService = CartWithOneItem();
        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        var shipping = ShippingReturning(RateCardFloor());

        pricing.Setup(p => p.CalculatePriceAsync(
            "DISCOUNT10",
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.Is<ShippingJourney?>(j => j!.ServiceTier == "KINETIX_INSTANT"),
            It.IsAny<string>()
        ))
            .ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 15000m, 0m, 15000m, 195000m, TwoOfProductOne));

        var orderService = NewOrderService(dbContext, cartService.Object, pricing.Object, shipping.Object);
        var request = new CheckoutRequest(
            "Jl. Sudirman No. 45, Jakarta",
            "DISCOUNT10",
            "KINETIX_INSTANT",
            Buyer,
            BuyerPhone
        );

        var result = await orderService.CheckoutAsync(Customer, request, "IDEMP-KEY-12345");

        Assert.NotNull(result);
        Assert.StartsWith("ORD-", result.OrderNumber);
        Assert.Equal("PAID", result.Status);
        Assert.Equal(200000m, result.Subtotal);
        Assert.Equal(20000m, result.DiscountAmount);
        Assert.Equal(15000m, result.BaseShippingFee);
        Assert.Equal(195000m, result.FinalTotal);
        Assert.Equal("KINETIX_INSTANT", result.ShippingServiceTier);
        Assert.Equal(0.0, result.DistanceKm);

        cartService.Verify(s => s.ClearCartAsync(Customer), Times.Once);
        pricing.Verify(
            p => p.CalculatePriceAsync(
                "DISCOUNT10",
                It.IsAny<IReadOnlyList<PriceLine>>(),
                It.Is<ShippingJourney?>(j => j!.ServiceTier == "KINETIX_INSTANT"),
                It.IsAny<string>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task CheckoutAsync_AVoucherPricingDidNotApply_IsNeitherRecordedNorRedeemed() {
        using var dbContext = GetInMemoryDbContext();
        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            "OTHER-SHOP",
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            Merchant
        )).ReturnsAsync(new PriceCalculationResult(
            200000m,
            0m,
            15000m,
            0m,
            15000m,
            215000m,
            TwoOfProductOne,
            AppliedVoucher: null
        ));
        var voucher = new Mock<IVoucherQuotaClient>();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            voucherClient: voucher.Object
        );
        var request = new CheckoutRequest(
            "Jl. Sudirman No. 45, Jakarta",
            "OTHER-SHOP",
            "KINETIX_INSTANT",
            Buyer,
            BuyerPhone
        );

        var result = await orderService.CheckoutAsync(Customer, request, "IDEMP-VOUCHER-ELSEWHERE");

        Assert.Equal("PAID", result.Status);
        Assert.Null(Assert.Single(dbContext.Orders).AppliedVoucher);
        voucher.Verify(
            c => c.RedeemVoucherAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_AVoucherPricingApplied_IsRedeemedAgainstTheCatalogsMerchant() {
        using var dbContext = GetInMemoryDbContext();
        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            "SHOP10",
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            Merchant
        )).ReturnsAsync(new PriceCalculationResult(
            200000m,
            20000m,
            15000m,
            0m,
            15000m,
            195000m,
            TwoOfProductOne,
            AppliedVoucher: "SHOP10"
        ));
        var voucher = new Mock<IVoucherQuotaClient>();
        voucher.Setup(c => c.RedeemVoucherAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        ))
            .ReturnsAsync(StepResult.Ok());

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            voucherClient: voucher.Object
        );
        var request = new CheckoutRequest(
            "Jl. Sudirman No. 45, Jakarta",
            "SHOP10",
            "KINETIX_INSTANT",
            Buyer,
            BuyerPhone
        );

        var result = await orderService.CheckoutAsync(Customer, request, "IDEMP-VOUCHER-HERE");

        Assert.Equal("PAID", result.Status);
        Assert.Equal("SHOP10", Assert.Single(dbContext.Orders).AppliedVoucher);
        voucher.Verify(
            c => c.RedeemVoucherAsync("SHOP10", result.OrderNumber, Customer, Merchant),
            Times.Once
        );
    }

    [Fact]
    public async Task CheckoutAsync_AMerchantIdentitySaysMayNotSell_IsRefusedBeforeAnythingIsPricedOrHeld() {
        using var dbContext = GetInMemoryDbContext();
        var pricing = PricingPassingShippingThrough();
        var suspended = new Mock<IMerchantStanding>();
        suspended.Setup(m => m.MaySellAsync(Merchant, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            merchants: suspended.Object
        );
        var request = new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_INSTANT", Buyer, BuyerPhone);

        await Assert.ThrowsAsync<MerchantMayNotSellException>(
            () => orderService.CheckoutAsync(Customer, request, "IDEMP-SUSPENDED")
        );

        Assert.Empty(dbContext.Orders);
        pricing.Verify(
            p => p.CalculatePriceAsync(
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<PriceLine>>(),
                It.IsAny<ShippingJourney?>(),
                It.IsAny<string>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenIdentityCannotSayWhetherTheMerchantMaySell_RefusesRatherThanAssuming() {
        using var dbContext = GetInMemoryDbContext();
        var unknown = new Mock<IMerchantStanding>();
        unknown.Setup(m => m.MaySellAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MerchantStandingUnknownException(
                Merchant,
                new InvalidOperationException("identity is down")
            ));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object,
            merchants: unknown.Object
        );
        var request = new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, "KINETIX_INSTANT", Buyer, BuyerPhone);

        await Assert.ThrowsAsync<MerchantStandingUnknownException>(
            () => orderService.CheckoutAsync(Customer, request, "IDEMP-IDENTITY-DOWN")
        );

        Assert.Empty(dbContext.Orders);
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

        var orderService = NewOrderService(
            dbContext,
            new Mock<ICartService>().Object,
            new Mock<IPricingClient>().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var result = await orderService.GetOrderByIdAsync(order.Id);

        Assert.Equal("CLIENT_SUPPLIED", result!.ShippingQuoteBasis);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheSelectedOptionCarriesARealDistance_RecordsDistanceQuoted() {
        using var dbContext = GetInMemoryDbContext();

        var farAway = new EstimateShippingResult(660.0, [
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                660.0,
                "1 - 3 Hari",
                true,
                null
            ),
        ]);

        var pricing = new Mock<IPricingClient>();
        pricing.Setup(p => p.QuoteShippingAsync(It.IsAny<IReadOnlyList<ShippingJourney>>()))
            .ReturnsAsync((IReadOnlyList<ShippingJourney> journeys) =>
                [.. journeys.Select(j => new QuotedShippingResult(
                    j.ServiceTier,
                    j.DistanceKm == 660.0 ? 58500m : 9000m,
                    true
                ))]
            );
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.Is<ShippingJourney?>(j => j!.DistanceKm == 660.0),
            It.IsAny<string>()
        )).ReturnsAsync(new PriceCalculationResult(200000m, 20000m, 58500m, 0m, 58500m, 238500m, TwoOfProductOne));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(farAway).Object
        );

        var result = await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-DISTANCEQUOTED"
        );

        Assert.Equal(660.0, result.DistanceKm);
        Assert.Equal(58500m, result.BaseShippingFee);
        Assert.Equal("DISTANCE_QUOTED", result.ShippingQuoteBasis);

        pricing.Verify(
            p => p.QuoteShippingAsync(
                It.Is<IReadOnlyList<ShippingJourney>>(js => js.Any(j => j.DistanceKm == 660.0))
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task CheckoutAsync_ReadsTheBasisFromTheDistanceItRecords_NotTheResponseEnvelope() {
        using var dbContext = GetInMemoryDbContext();

        var envelopeDisagrees = new EstimateShippingResult(0.0, [
            new ShippingOptionResult(
                "KINETIX_REGULAR",
                "Kinetix Regular Freight",
                660.0,
                "1 - 3 Hari",
                true,
                null
            ),
        ]);

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(envelopeDisagrees).Object
        );

        var result = await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-ENVELOPE"
        );

        Assert.Equal(660.0, result.DistanceKm);
        Assert.Equal("DISTANCE_QUOTED", result.ShippingQuoteBasis);
    }

    [Fact]
    public async Task CheckoutAsync_WhenPricingsTotalIsNotTheMerchantPlusTheShipping_RefusesBeforeCharging() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        ))
            .ReturnsAsync(new PriceCalculationResult(200000m, 0m, 9000m, 0m, 9000m, 1m, TwoOfProductOne));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<OrderAmountsUnchargeableException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-TOTALGAP"
            ));

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheVoucherDiscountExceedsTheSubtotal_RefusesBeforeCharging() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        ))
            .ReturnsAsync(new PriceCalculationResult(200000m, 300000m, 9000m, 0m, 9000m, -91000m, TwoOfProductOne));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<OrderAmountsUnchargeableException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-OVERDISCOUNT"
            ));

        Assert.Empty(dbContext.Orders);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenAnAmountCannotBeChargedInWholeMinorUnits_RefusesWithoutWritingAnOrder() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        ))
            .ReturnsAsync(new PriceCalculationResult(
                200000m,
                20000m,
                9000m,
                0.996m,
                8999.004m,
                188999.004m,
                TwoOfProductOne
            ));

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<OrderAmountsUnchargeableException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-SUBMINOR"
            ));

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_AnEmptyCartAndACartWithNoMerchantAreNotTheSameFailure() {
        static Mock<ICartService> CartWithNoMerchant() {
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
            return cartService;
        }

        static Mock<ICartService> EmptyCart() {
            var cartService = new Mock<ICartService>();
            cartService.Setup(s => s.GetCartAsync(Customer)).ReturnsAsync(new CustomerCart(Customer));
            return cartService;
        }

        async Task<Exception?> Outcome(ICartService cart, string key) {
            using var dbContext = GetInMemoryDbContext();
            var orderService = NewOrderService(
                dbContext,
                cart,
                PricingPassingShippingThrough().Object,
                ShippingReturning(RateCardFloor()).Object,
                null,
                CatalogNaming(("PRODUCT-01", "")).Object
            );

            return await Record.ExceptionAsync(() => orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                key
            ));
        }

        var empty = await Outcome(EmptyCart().Object, "IDEMP-KEY-EMPTYCART");
        var merchantless = await Outcome(CartWithNoMerchant().Object, "IDEMP-KEY-NOMERCHANT-2");

        Assert.NotNull(empty);
        Assert.NotNull(merchantless);
        Assert.NotEqual(empty.GetType(), merchantless.GetType());
    }

    private static Mock<ICartService> CartClaiming(decimal unitPrice, string title) {
        var cartService = new Mock<ICartService>();
        var cart = new CustomerCart(Customer);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = title,
            UnitPrice = unitPrice,
            Quantity = 2,
            MerchantPrincipalId = Merchant
        });
        cartService.Setup(s => s.GetCartAsync(Customer)).ReturnsAsync(cart);
        return cartService;
    }

    private static Mock<IPricingClient> PricingReturning(decimal subtotal, IReadOnlyList<PricedLine> lines) {
        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync(new PriceCalculationResult(subtotal, 0m, 9000m, 0m, 9000m, subtotal + 9000m, lines));
        return pricing;
    }

    [Fact]
    public async Task CheckoutAsync_RecordsEachItemAtThePriceTheBuyerWasCharged() {
        using var dbContext = GetInMemoryDbContext();
        var pricing = PricingReturning(150000m, [new PricedLine("PRODUCT-01", 2, "FLASH-7", 75000m, 150000m)]);

        var orderService = NewOrderService(
            dbContext,
            CartClaiming(1m, "Whatever the buyer typed").Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object
        );

        await orderService.CheckoutAsync(
            Customer,
            new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
            "IDEMP-KEY-ITEM-PRICES"
        );

        var item = Assert.Single(dbContext.OrderItems);
        Assert.Equal(75000m, item.UnitPrice);
        Assert.Equal(150000m, item.LineSubtotal);
        Assert.Equal(2, item.Quantity);
        Assert.Equal("Sample Product", item.ProductTitle);
    }

    [Fact]
    public async Task CheckoutAsync_WhenPricingLeavesAnItemUnpriced_Refuses() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingReturning(200000m, []).Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<OrderAmountsUnchargeableException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-UNPRICED-ITEM"
            )
        );

        Assert.Empty(dbContext.Orders);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_WhenPricingPricesADifferentQuantity_Refuses() {
        using var dbContext = GetInMemoryDbContext();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingReturning(100000m, [new PricedLine("PRODUCT-01", 1, null, 100000m, 100000m)]).Object,
            ShippingReturning(RateCardFloor()).Object
        );

        await Assert.ThrowsAsync<OrderAmountsUnchargeableException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-WRONG-QUANTITY"
            )
        );

        Assert.Empty(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_WhenTheLinesDoNotAddUpToTheSubtotal_Refuses() {
        using var dbContext = GetInMemoryDbContext();

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingReturning(200000m, [new PricedLine("PRODUCT-01", 2, null, 90000m, 180000m)]).Object,
            ShippingReturning(RateCardFloor()).Object
        );

        await Assert.ThrowsAsync<OrderAmountsUnchargeableException>(() =>
            orderService.CheckoutAsync(
                Customer,
                new CheckoutRequest("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone),
                "IDEMP-KEY-LINES-SHORT"
            )
        );

        Assert.Empty(dbContext.Orders);
    }

    private static CheckoutRequest ADeliverableRequest() =>
        new("Jl. Sudirman No. 45, Jakarta", null, null, Buyer, BuyerPhone);

    private static Mock<IPricingClient> PricingApplying(string voucher) {
        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync(
            new PriceCalculationResult(200000m, 20000m, 9000m, 0m, 9000m, 189000m, TwoOfProductOne, voucher)
        );
        return pricing;
    }

    private static Mock<IVoucherQuotaClient> VoucherRefusing() {
        var voucher = new Mock<IVoucherQuotaClient>();
        voucher.Setup(c => c.RedeemVoucherAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()
        )).ReturnsAsync(StepResult.Refused("the voucher's quota is spent"));
        return voucher;
    }

    [Fact]
    public async Task CheckoutAsync_AnEmptyIdempotencyKeyIsStoredAsNoKeyAtAll() {
        using var dbContext = GetInMemoryDbContext();
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var result = await orderService.CheckoutAsync(Customer, ADeliverableRequest(), "");

        var stored = await dbContext.Orders.SingleAsync(o => o.OrderNumber == result.OrderNumber);
        Assert.Null(stored.IdempotencyKey);
    }

    [Fact]
    public async Task CheckoutAsync_ACartThatCannotBeClearedAfterPaymentStillAnswersThePaidOrder() {
        using var dbContext = GetInMemoryDbContext();
        var cartService = CartWithOneItem();
        cartService.Setup(s => s.ClearCartAsync(Customer)).ThrowsAsync(new InvalidOperationException("redis is down"));

        var orderService = NewOrderService(
            dbContext,
            cartService.Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var result = await orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-CART-STUCK");

        Assert.Equal("PAID", result.Status);
        Assert.Equal(OrderStatus.PAID, (await dbContext.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task CheckoutAsync_AZeroTotalIsRefusedBeforeAnyStepOrOrderRow() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync(
            new PriceCalculationResult(200000m, 200000m, 9000m, 9000m, 0m, 0m, TwoOfProductOne, "FREE-ALL")
        );

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        await Assert.ThrowsAsync<OrderTotalNotChargeableException>(() =>
            orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-ZERO-TOTAL")
        );

        Assert.Empty(dbContext.Orders);
        Assert.Empty(dbContext.CheckoutSagas);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task CheckoutAsync_AShippingOnlyTotalIsCharged() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var pricing = new Mock<IPricingClient>();
        QuotingFromTheRateCard(pricing);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync(
            new PriceCalculationResult(200000m, 200000m, 9000m, 0m, 9000m, 9000m, TwoOfProductOne, "FREE-GOODS")
        );

        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        var result = await orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-SHIPPING-ONLY");

        Assert.Equal("PAID", result.Status);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                Customer,
                Merchant,
                It.IsAny<string?>(),
                9000m,
                0m,
                9000m
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task CheckoutAsync_ReplayingTheKeyOfARolledBackCheckoutDoesNotAnswerAsIfItWerePlaced() {
        using var dbContext = GetInMemoryDbContext();
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingApplying("DISCOUNT10").Object,
            ShippingReturning(RateCardFloor()).Object,
            voucherClient: VoucherRefusing().Object
        );

        await Assert.ThrowsAsync<CheckoutFailedException>(() =>
            orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-ROLLED-BACK")
        );

        var replay = await Assert.ThrowsAsync<CheckoutFailedException>(() =>
            orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-ROLLED-BACK")
        );

        Assert.True(replay.Replayed);
        Assert.True(replay.RolledBack);
        Assert.Contains("voucher", replay.Reason);
        Assert.Single(dbContext.Orders);
        Assert.Single(dbContext.CheckoutSagas);
    }

    [Fact]
    public async Task CheckoutAsync_ReplayingThePaidCheckoutAnswersTheSameOrderWithoutRunningAgain() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object,
            escrow.Object
        );

        var first = await orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-REPLAY-PAID");
        var again = await orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-REPLAY-PAID");

        Assert.Equal(first.OrderNumber, again.OrderNumber);
        Assert.Equal("PAID", again.Status);
        escrow.Verify(
            c => c.CreateHoldAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task CheckoutAsync_AKeyRepeatedWithADifferentRequestIsRefused() {
        using var dbContext = GetInMemoryDbContext();
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var first = await orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-REUSED");
        var elsewhere = ADeliverableRequest() with { ShippingAddress = "Jl. Thamrin No. 1, Jakarta" };

        var refusal = await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() =>
            orderService.CheckoutAsync(Customer, elsewhere, "IDEMP-KEY-REUSED")
        );

        Assert.Equal(first.OrderNumber, refusal.OrderNumber);
        Assert.Single(dbContext.Orders);
    }

    [Fact]
    public async Task CheckoutAsync_ReplayingAKeyWhoseCheckoutHasNotFinishedSaysSo() {
        using var dbContext = GetInMemoryDbContext();
        var running = new OrderEntity {
            OrderNumber = "ORD-20261021-RUNNING",
            CustomerPrincipalId = Customer,
            MerchantPrincipalId = Merchant,
            Status = OrderStatus.PENDING_PAYMENT,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta",
            RecipientName = Buyer,
            RecipientPhone = BuyerPhone,
            IdempotencyKey = "IDEMP-KEY-RUNNING",
        };
        dbContext.Orders.Add(running);
        await dbContext.SaveChangesAsync();

        var cart = CartWithOneItem();
        var orderService = NewOrderService(
            dbContext,
            cart.Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var refusal = await Assert.ThrowsAsync<CheckoutInProgressException>(() =>
            orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-RUNNING")
        );

        Assert.Equal("ORD-20261021-RUNNING", refusal.OrderNumber);
        cart.Verify(c => c.GetCartAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_AnOverlongIdempotencyKeyIsRefusedBeforeAnythingIsAsked() {
        using var dbContext = GetInMemoryDbContext();
        var cart = CartWithOneItem();
        var orderService = NewOrderService(
            dbContext,
            cart.Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var refusal = await Assert.ThrowsAsync<FieldTooLongException>(() =>
            orderService.CheckoutAsync(Customer, ADeliverableRequest(), new string('k', 101))
        );

        Assert.Equal("Idempotency-Key", refusal.Field);
        Assert.Equal(100, refusal.MaxLength);
        cart.Verify(c => c.GetCartAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckoutAsync_ACartHoldingTheBuyersOwnProductIsRefusedBeforeAnythingIsHeld() {
        using var dbContext = GetInMemoryDbContext();
        var escrow = AcceptingEscrow();
        var shipping = ShippingReturning(RateCardFloor());
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            shipping.Object,
            escrow.Object,
            CatalogNaming(("PRODUCT-01", Customer)).Object
        );

        var refusal = await Assert.ThrowsAsync<OwnProductNotPurchasableException>(() =>
            orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-OWN-PRODUCT")
        );

        Assert.Equal("PRODUCT-01", refusal.ProductId);
        Assert.Empty(dbContext.Orders);
        shipping.Verify(
            c => c.EstimateShippingOptionsAsync(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<long>(),
                It.IsAny<string>()
            ),
            Times.Never
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(1000)]
    public async Task CheckoutAsync_ACartLineOutsideTheQuantityRangeIsRefusedBeforePricing(int quantity) {
        using var dbContext = GetInMemoryDbContext();
        var pricing = PricingPassingShippingThrough();
        var cart = new Mock<ICartService>();
        var stored = new CustomerCart(Customer);
        stored.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "Sample Product",
            UnitPrice = 100000m,
            Quantity = quantity,
            MerchantPrincipalId = Merchant,
        });
        cart.Setup(c => c.GetCartAsync(Customer)).ReturnsAsync(stored);

        var orderService = NewOrderService(
            dbContext,
            cart.Object,
            pricing.Object,
            ShippingReturning(RateCardFloor()).Object
        );

        var refusal = await Assert.ThrowsAsync<CartLineQuantityOutOfRangeException>(() =>
            orderService.CheckoutAsync(Customer, ADeliverableRequest(), "IDEMP-KEY-LEGACY-QUANTITY")
        );

        Assert.Equal(quantity, refusal.Quantity);
        pricing.Verify(
            p => p.CalculatePriceAsync(
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<PriceLine>>(),
                It.IsAny<ShippingJourney?>(),
                It.IsAny<string>()
            ),
            Times.Never
        );
    }

    [Theory]
    [InlineData(501, 128, 32, 50, "shippingAddress")]
    [InlineData(500, 129, 32, 50, "recipientName")]
    [InlineData(500, 128, 33, 50, "recipientPhone")]
    [InlineData(500, 128, 32, 51, "voucherCode")]
    public async Task CheckoutAsync_AFieldLongerThanTheOrderCanRecordIsRefusedBeforeAnythingIsAsked(
        int addressLength,
        int nameLength,
        int phoneLength,
        int voucherLength,
        string field
    ) {
        using var dbContext = GetInMemoryDbContext();
        var geocoder = GeocoderPlacing(AddressPlacement.Placed(Home));
        var orderService = NewOrderService(
            dbContext,
            CartWithOneItem().Object,
            PricingPassingShippingThrough().Object,
            ShippingReturning(RateCardFloor()).Object,
            geocoder: geocoder.Object
        );
        var request = new CheckoutRequest(
            new string('a', addressLength),
            new string('v', voucherLength),
            null,
            new string('b', nameLength),
            new string('0', phoneLength)
        );

        var refusal = await Assert.ThrowsAsync<FieldTooLongException>(() =>
            orderService.CheckoutAsync(Customer, request, "IDEMP-KEY-LONG")
        );

        Assert.Equal(field, refusal.Field);

        geocoder.Verify(g => g.PlaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(dbContext.Orders);
    }
}
