using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Grpc;
using Kinetix.OrderService.Infrastructure.Http;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using OrderApplicationService = Kinetix.OrderService.Application.Services.OrderService;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Tests;

public class OrderPartiesTests {
    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";
    private const string Courier = "6d2c8b14-0f7e-4a39-9c51-8e3a7b2f4d60";

    [Fact]
    public async Task AnOrderNamesItsMerchantAndTheCourierWhoDeliveredIt() {
        using var db = InMemoryDb();
        var order = await PlaceAsync(db, "ORD-20261013-0001", OrderStatus.DELIVERED);

        db.ShippingSettlements.Add(new ShippingSettlement {
            OrderNumber = order.OrderNumber,
            DriverPrincipalId = Courier,
            DeliveredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var read = await ServiceOver(db).GetOrderByIdAsync(order.Id);

        Assert.Equal(Merchant, read!.MerchantPrincipalId);
        Assert.Equal(Courier, read.DriverPrincipalId);
    }

    [Fact]
    public async Task AnOrderNoCourierHasDeliveredNamesNoCourier() {
        using var db = InMemoryDb();
        var order = await PlaceAsync(db, "ORD-20261013-0002", OrderStatus.PROCESSING_FULFILLMENT);

        var read = await ServiceOver(db).GetOrderByIdAsync(order.Id);

        Assert.Equal(Merchant, read!.MerchantPrincipalId);
        Assert.Null(read.DriverPrincipalId);
    }

    [Fact]
    public async Task EachOrderInACustomersListNamesItsOwnCourier() {
        using var db = InMemoryDb();
        var delivered = await PlaceAsync(db, "ORD-20261013-0003", OrderStatus.DELIVERED);
        var packing = await PlaceAsync(db, "ORD-20261013-0004", OrderStatus.PROCESSING_FULFILLMENT);

        db.ShippingSettlements.Add(new ShippingSettlement {
            OrderNumber = delivered.OrderNumber,
            DriverPrincipalId = Courier,
            DeliveredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var page = await ServiceOver(db).GetCustomerOrdersAsync(
            Customer,
            null,
            1,
            10
        );

        var byNumber = page.Orders.ToDictionary(o => o.OrderNumber);

        Assert.Equal(Courier, byNumber[delivered.OrderNumber].DriverPrincipalId);
        Assert.Null(byNumber[packing.OrderNumber].DriverPrincipalId);
    }

    [Fact]
    public async Task GetOrderDetailsSendsTheMerchantAndTheCourier() {
        var details = await DetailsOf(AnswerNaming(Courier));

        Assert.True(details.Found);
        Assert.Equal(Merchant, details.MerchantPrincipalId);
        Assert.Equal(Courier, details.DriverPrincipalId);
    }

    [Fact]
    public async Task GetOrderDetailsSendsNoCourierBeforeOneDelivered() {
        var details = await DetailsOf(AnswerNaming(null));

        Assert.True(details.Found);
        Assert.Equal(string.Empty, details.DriverPrincipalId);
    }

    private static async Task<OrderProto.GetOrderDetailsResponse> DetailsOf(OrderResponse stored) {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.GetOrderByIdAsync(stored.Id)).ReturnsAsync(stored);

        var server = new OrderGrpcServerService(
            orders.Object,
            new Mock<IFulfillmentPackedHandler>().Object,
            new Mock<IOrderDeliveredHandler>().Object,
            new Mock<IReturnsHandler>().Object
        );

        return await server.GetOrderDetails(
            new OrderProto.GetOrderDetailsRequest { OrderId = stored.Id.ToString() },
            null!
        );
    }

    private static OrderResponse AnswerNaming(string? courier) => new(
        Guid.NewGuid(),
        "ORD-20261013-0005",
        Customer,
        Merchant,
        courier,
        "DELIVERED",
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

    private static async Task<OrderEntity> PlaceAsync(OrderDbContext db, string orderNumber, OrderStatus status) {
        var order = new OrderEntity {
            OrderNumber = orderNumber,
            CustomerPrincipalId = Customer,
            MerchantPrincipalId = Merchant,
            Status = status,
            Subtotal = 150000m,
            FinalTotal = 165000m,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta"
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return order;
    }

    private static OrderDbContext InMemoryDb() {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new OrderDbContext(options);
    }

    private static OrderApplicationService ServiceOver(OrderDbContext db) {
        var policy = new CompensationPolicy();

        var runner = new CheckoutSagaRunner(
            db,
            new Mock<IVoucherQuotaClient>().Object,
            new Mock<IFlashSaleClient>().Object,
            new Mock<IStockClient>().Object,
            new Mock<IEscrowClient>().Object,
            new Mock<IFulfillmentClient>().Object,
            new FakeSagaLeaseStore(db, policy),
            policy,
            new RequestIdAccessor(new HttpContextAccessor()),
            NullLogger<CheckoutSagaRunner>.Instance
        );

        return new OrderApplicationService(
            db,
            new Mock<ICartService>().Object,
            new Mock<IProductDirectory>().Object,
            new Mock<IMerchantStanding>().Object,
            new Mock<IPricingClient>().Object,
            new Mock<IShippingClient>().Object,
            new Mock<IAddressDirectory>().Object,
            new Mock<IAddressGeocoder>().Object,
            runner,
            NullLogger<OrderApplicationService>.Instance
        );
    }
}
