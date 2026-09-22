using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Grpc;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;
using OrderApplicationService = Kinetix.OrderService.Application.Services.OrderService;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Tests;

public class OrderChangeFeedTests {
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly DateTime Noon = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options
        );

    private static OrderApplicationService FeedOver(OrderDbContext db) =>
        new(db, new Mock<ICartService>().Object, new Mock<IPricingClient>().Object,
            new Mock<IShippingClient>().Object, null!,
            NullLogger<Kinetix.OrderService.Application.Services.OrderService>.Instance
        );

    private static OrderEntity AnOrder(string number, DateTime updatedAt, string merchant = Merchant) =>
        new() {
            OrderNumber = number,
            CustomerPrincipalId = Buyer,
            MerchantPrincipalId = merchant,
            Status = OrderStatus.PAID,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta",
            RecipientName = "Test Buyer",
            RecipientPhone = "081200000000",
            CreatedAt = updatedAt.AddMinutes(-5),
            UpdatedAt = updatedAt,
            Items = [
                new OrderItem { ProductId = "SKU-1", ProductTitle = "Sepatu Kulit", UnitPrice = 100m, Quantity = 1, LineSubtotal = 100m }
            ]
        };

    private static OrderGrpcServerService ServerOver(IOrderService orders) =>
        new(orders, new Mock<IFulfillmentPackedHandler>().Object,
            new Mock<IOrderDeliveredHandler>().Object, new Mock<IReturnsHandler>().Object
        );

    [Fact]
    public async Task OrdersChangedSince_BreaksATimestampTieOnTheOrderNumber_RatherThanLosingRows() {
        using var db = NewDbContext();
        db.Orders.AddRange(
            AnOrder("ORD-A", Noon), AnOrder("ORD-B", Noon), AnOrder("ORD-C", Noon)
        );
        await db.SaveChangesAsync();

        var service = FeedOver(db);

        var first = await service.OrdersChangedSinceAsync(null, string.Empty, 2);
        var second = await service.OrdersChangedSinceAsync(
            first.Changes[^1].UpdatedAt, first.Changes[^1].OrderNumber, 2
        );

        Assert.Equal(["ORD-A", "ORD-B"], first.Changes.Select(c => c.OrderNumber));
        Assert.True(first.HasMore);
        Assert.Equal(["ORD-C"], second.Changes.Select(c => c.OrderNumber));
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task OrdersChangedSince_WalksInUpdateOrder_NotInPlacementOrder() {
        using var db = NewDbContext();
        var late = AnOrder("ORD-OLD", Noon.AddHours(1));
        late.CreatedAt = Noon.AddDays(-30);
        db.Orders.AddRange(AnOrder("ORD-NEW", Noon), late);
        await db.SaveChangesAsync();

        var page = await FeedOver(db).OrdersChangedSinceAsync(null, string.Empty, 10);

        Assert.Equal(["ORD-NEW", "ORD-OLD"], page.Changes.Select(c => c.OrderNumber));
    }

    [Fact]
    public async Task OrdersChangedSince_ReportsTheMerchantTheOrderWasPlacedWith() {
        using var db = NewDbContext();
        db.Orders.Add(AnOrder("ORD-A", Noon));
        await db.SaveChangesAsync();

        var page = await FeedOver(db).OrdersChangedSinceAsync(null, string.Empty, 10);

        Assert.Equal(Merchant, page.Changes[0].MerchantPrincipalId);
        Assert.Equal(Buyer, page.Changes[0].BuyerPrincipalId);
        Assert.Equal(["Sepatu Kulit"], page.Changes[0].LineTitles);
    }

    [Fact]
    public async Task OrdersChangedSince_LeavesAnUnknownMerchantUnknown_RatherThanGuessing() {
        using var db = NewDbContext();
        db.Orders.Add(AnOrder("ORD-LEGACY", Noon, merchant: string.Empty));
        await db.SaveChangesAsync();

        var page = await FeedOver(db).OrdersChangedSinceAsync(null, string.Empty, 10);

        Assert.Equal(string.Empty, page.Changes[0].MerchantPrincipalId);
    }

    [Fact]
    public async Task TheEdgeCapsAPageACallerAsksToBeEnormous() {
        var orders = new Mock<IOrderService>();
        int asked = 0;
        orders.Setup(o => o.OrdersChangedSinceAsync(
                It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback((DateTime? _, string _, int limit) => asked = limit)
            .ReturnsAsync(new OrderChangePage([], false));

        await ServerOver(orders.Object).OrdersChangedSince(
            new OrderProto.OrdersChangedSinceRequest { Limit = 1_000_000 }, null!
        );

        Assert.Equal(500, asked);
    }

    [Fact]
    public async Task TheEdgeReadsAWholePageWhenTheCallerNamesNoSize() {
        var orders = new Mock<IOrderService>();
        int asked = 0;
        orders.Setup(o => o.OrdersChangedSinceAsync(
                It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback((DateTime? _, string _, int limit) => asked = limit)
            .ReturnsAsync(new OrderChangePage([], false));

        await ServerOver(orders.Object).OrdersChangedSince(
            new OrderProto.OrdersChangedSinceRequest(), null!
        );

        Assert.Equal(100, asked);
    }

    [Fact]
    public async Task TheEdgeKeepsTheCallersCursorWhenNothingChanged() {
        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.OrdersChangedSinceAsync(
                It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new OrderChangePage([], false));

        var response = await ServerOver(orders.Object).OrdersChangedSince(
            new OrderProto.OrdersChangedSinceRequest {
                Cursor = new OrderProto.OrderCursor {
                    UpdatedThrough = Timestamp.FromDateTime(Noon),
                    LastOrderNumber = "ORD-B"
                }
            },
            null!
        );

        Assert.Equal("ORD-B", response.Next.LastOrderNumber);
        Assert.Equal(Noon, response.Next.UpdatedThrough.ToDateTime());
        Assert.False(response.HasMore);
    }

    [Fact]
    public async Task TheEdgeHandsBackACursorBuiltFromTheLastRowOfThePage() {
        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.OrdersChangedSinceAsync(
                It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new OrderChangePage([
                new OrderChange("ORD-A", Buyer, Merchant, OrderStatus.PAID, ["Sepatu Kulit"], Noon.AddMinutes(-5), Noon),
                new OrderChange("ORD-B", Buyer, Merchant, OrderStatus.SHIPPED, ["Tas"], Noon, Noon.AddMinutes(3))
            ], true));

        var response = await ServerOver(orders.Object).OrdersChangedSince(
            new OrderProto.OrdersChangedSinceRequest(), null!
        );

        Assert.True(response.HasMore);
        Assert.Equal("ORD-B", response.Next.LastOrderNumber);
        Assert.Equal(Noon.AddMinutes(3), response.Next.UpdatedThrough.ToDateTime());
    }

    [Fact]
    public async Task TheEdgeCarriesTheStatusAndTheLineTitlesAcross() {
        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.OrdersChangedSinceAsync(
                It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new OrderChangePage([
                new OrderChange("ORD-A", Buyer, Merchant, OrderStatus.SHIPPED, ["Sepatu Kulit", "Tas"], Noon.AddMinutes(-5), Noon)
            ], false));

        var response = await ServerOver(orders.Object).OrdersChangedSince(
            new OrderProto.OrdersChangedSinceRequest(), null!
        );

        var record = Assert.Single(response.Upserted);
        Assert.Equal("ORD-A", record.OrderNumber);
        Assert.Equal(Buyer, record.BuyerPrincipalId);
        Assert.Equal(Merchant, record.MerchantPrincipalId);
        Assert.Equal(Common.V1.OrderStatus.InTransit, record.Status);
        Assert.Equal(["Sepatu Kulit", "Tas"], record.LineTitles);
        Assert.Equal(Noon, record.UpdatedAt.ToDateTime());
        Assert.Equal(Noon.AddMinutes(-5), record.PlacedAt.ToDateTime());
    }

    [Fact]
    public async Task ThePostgresProviderCanTranslateTheCursor() {
        string? connectionString = Environment.GetEnvironmentVariable("KINETIX_TEST_POSTGRES");
        if (connectionString is null) {
            return;
        }

        using var db = new OrderDbContext(
            new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(connectionString).Options
        );

        var page = await FeedOver(db).OrdersChangedSinceAsync(Noon, "ORD-A", 5);

        Assert.NotNull(page);
    }
}
