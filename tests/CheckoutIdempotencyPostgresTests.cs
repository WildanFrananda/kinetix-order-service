using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Products;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.Infrastructure.Http;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderApplicationService = Kinetix.OrderService.Application.Services.OrderService;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class CheckoutIdempotencyPostgresTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly CheckoutRequest Request =
        new("Jl. Sudirman No. 45, Jakarta", null, null, "Test Buyer", "081200000000");

    private static Mock<ICartService> CartOf(string buyer) {
        var cart = new CustomerCart(buyer);
        cart.Items.Add(new CartItem {
            ProductId = "PRODUCT-01",
            ProductTitle = "Sample Product",
            UnitPrice = 100000m,
            Quantity = 2,
            MerchantPrincipalId = Merchant,
        });

        var carts = new Mock<ICartService>();
        carts.Setup(c => c.GetCartAsync(buyer)).ReturnsAsync(cart);
        return carts;
    }

    private static Mock<IPricingClient> Pricing() {
        var pricing = new Mock<IPricingClient>();
        pricing.Setup(p => p.QuoteShippingAsync(It.IsAny<IReadOnlyList<ShippingJourney>>()))
            .ReturnsAsync([new QuotedShippingResult("KINETIX_REGULAR", 9000m, true)]);
        pricing.Setup(p => p.CalculatePriceAsync(
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<PriceLine>>(),
            It.IsAny<ShippingJourney?>(),
            It.IsAny<string>()
        )).ReturnsAsync(new PriceCalculationResult(
            200000m,
            0m,
            9000m,
            0m,
            9000m,
            209000m,
            [new PricedLine("PRODUCT-01", 2, null, 100000m, 200000m)]
        ));
        return pricing;
    }

    private static OrderApplicationService Checkout(OrderDbContext db, ICartService carts, IPricingClient pricing) {
        var catalog = new Mock<IProductDirectory>();
        catalog.Setup(c => c.GetProductAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sku, CancellationToken _) =>
                new CatalogProduct(sku, Merchant, "Sample Product", 100000m, null, 500));

        var merchants = new Mock<IMerchantStanding>();
        merchants.Setup(m => m.MaySellAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var shipping = new Mock<IShippingClient>();
        shipping.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<double>(),
            It.IsAny<long>(),
            It.IsAny<string>()
        )).ReturnsAsync(new EstimateShippingResult(4.2, [
            new ShippingOptionResult("KINETIX_REGULAR", "Kinetix Regular Freight", 4.2, "1 - 3 Hari", true, null),
        ]));

        var addresses = new Mock<IAddressDirectory>();
        addresses.Setup(a => a.PickupPointAsync(It.IsAny<string>())).ReturnsAsync(new MapPoint(-6.1754, 106.8272));

        var geocoder = new Mock<IAddressGeocoder>();
        geocoder.Setup(g => g.PlaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AddressPlacement.Placed(new MapPoint(-6.2088, 106.8456)));

        var stock = new Mock<IStockClient>();
        stock.Setup(c => c.ReserveStockAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<string>()
        )).ReturnsAsync(StepResult.Ok());

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

        var fulfilment = new Mock<IFulfillmentClient>();
        fulfilment.Setup(c => c.CreateTaskAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<FulfillmentLine>>()
        ))
            .ReturnsAsync(new FulfillmentCreated(StepResult.Ok(), "901"));

        var policy = CompensationPolicies.Standard();
        var runner = new CheckoutSagaRunner(
            db,
            new Mock<IVoucherQuotaClient>().Object,
            new Mock<IFlashSaleClient>().Object,
            stock.Object,
            escrow.Object,
            fulfilment.Object,
            new SagaLeaseStore(db, new SagaWorkerIdentity(), policy, NullLogger<SagaLeaseStore>.Instance),
            policy,
            new RequestIdAccessor(new HttpContextAccessor()),
            NullLogger<CheckoutSagaRunner>.Instance
        );

        return new OrderApplicationService(
            db,
            carts,
            catalog.Object,
            merchants.Object,
            pricing,
            shipping.Object,
            addresses.Object,
            geocoder.Object,
            runner,
            NullLogger<OrderApplicationService>.Instance
        );
    }

    private static async Task PlaceRivalAsync(string connectionString, string buyer, string key) {
        await using var rival = PostgresDatabase.NewDbContext(connectionString);
        var order = new OrderEntity {
            OrderNumber = $"ORD-RIVAL-{Guid.NewGuid():N}",
            CustomerPrincipalId = buyer,
            MerchantPrincipalId = Merchant,
            Status = OrderStatus.PENDING_PAYMENT,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta",
            RecipientName = "Test Buyer",
            RecipientPhone = "081200000000",
            IdempotencyKey = key,
        };

        rival.Orders.Add(order);
        rival.RecordPlaced(order, LifecycleActor.Buyer(buyer));
        await rival.SaveChangesAsync();
    }

    [Fact]
    public async Task ACheckoutThatLosesTheRaceForItsKeyAnswersWithTheWinnerRatherThanFailing() {
        if (PostgresDatabase.ConnectionString is not { } connectionString) {
            return;
        }

        var buyer = Guid.NewGuid().ToString();
        var key = $"IDEMP-RACE-{Guid.NewGuid():N}";
        var pricing = Pricing();
        pricing.Setup(p => p.QuoteShippingAsync(It.IsAny<IReadOnlyList<ShippingJourney>>()))
            .Returns(async () => {
                await PlaceRivalAsync(connectionString, buyer, key);
                return [new QuotedShippingResult("KINETIX_REGULAR", 9000m, true)];
            });

        await using var db = PostgresDatabase.NewDbContext(connectionString);
        var orders = Checkout(db, CartOf(buyer).Object, pricing.Object);

        var replay = await Assert.ThrowsAsync<CheckoutInProgressException>(() =>
            orders.CheckoutAsync(buyer, Request, key)
        );

        Assert.StartsWith("ORD-RIVAL-", replay.OrderNumber);
        Assert.Equal(1, await db.Orders.CountAsync(o => o.CustomerPrincipalId == buyer));
    }

    [Fact]
    public async Task ABuyerMayCheckOutTwiceWithAnEmptyKey() {
        if (PostgresDatabase.ConnectionString is not { } connectionString) {
            return;
        }

        var buyer = Guid.NewGuid().ToString();

        await using (var first = PostgresDatabase.NewDbContext(connectionString)) {
            await Checkout(first, CartOf(buyer).Object, Pricing().Object).CheckoutAsync(buyer, Request, "");
        }

        await using (var second = PostgresDatabase.NewDbContext(connectionString)) {
            await Checkout(second, CartOf(buyer).Object, Pricing().Object).CheckoutAsync(buyer, Request, "");
        }

        await using var db = PostgresDatabase.NewDbContext(connectionString);
        var paid = await db.Orders.CountAsync(o => o.CustomerPrincipalId == buyer && o.Status == OrderStatus.PAID);
        Assert.Equal(2, paid);
    }

    [Fact]
    public async Task TheKeyOfACheckoutThatNeverStartedItsSagaReplaysAsRolledBackOnceTheOrderIsCancelled() {
        if (PostgresDatabase.ConnectionString is not { } connectionString) {
            return;
        }

        var buyer = Guid.NewGuid().ToString();
        var key = $"IDEMP-SAGALESS-{Guid.NewGuid():N}";
        await SagalessCheckoutCancellerPostgresTests.PlaceSagalessAsync(
            connectionString,
            buyer,
            key,
            CheckoutFingerprint.Of(Request)
        );

        await using (var before = PostgresDatabase.NewDbContext(connectionString)) {
            await Assert.ThrowsAsync<CheckoutInProgressException>(() =>
                Checkout(before, CartOf(buyer).Object, Pricing().Object).CheckoutAsync(buyer, Request, key)
            );
        }

        await using (var sweep = PostgresDatabase.NewDbContext(connectionString)) {
            var canceller = new SagalessCheckoutCanceller(
                sweep,
                new OrderRowLock(sweep),
                NullLogger<SagalessCheckoutCanceller>.Instance
            );
            await canceller.CancelAsync(TimeSpan.FromMinutes(15), 1000, CancellationToken.None);
        }

        await using var after = PostgresDatabase.NewDbContext(connectionString);
        var replay = await Assert.ThrowsAsync<CheckoutFailedException>(() =>
            Checkout(after, CartOf(buyer).Object, Pricing().Object).CheckoutAsync(buyer, Request, key)
        );

        Assert.True(replay.Replayed);
        Assert.True(replay.RolledBack);
        Assert.Equal(SagalessCheckoutCanceller.Reason, replay.Reason);
    }
}
