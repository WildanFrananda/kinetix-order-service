using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Grpc;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderApplicationService = Kinetix.OrderService.Application.Services.OrderService;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class OrderChangeTransactionPostgresTests {
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static string? ConnectionString => PostgresDatabase.ConnectionString;

    private static string AnOrderNumber(string label) => $"ORD-PG-{label}-{Guid.NewGuid().ToString("N")[..8]}";

    private static OrderEntity AnOrder(string orderNumber) =>
        new() {
            OrderNumber = orderNumber,
            CustomerPrincipalId = Buyer,
            MerchantPrincipalId = Merchant,
            Status = OrderStatus.PENDING_PAYMENT,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
            Items = [
                new OrderItem {
                    ProductId = "SKU-1",
                    ProductTitle = $"Sepatu {orderNumber}",
                    UnitPrice = 100m,
                    Quantity = 1,
                    LineSubtotal = 100m,
                }
            ]
        };

    private static void Place(OrderDbContext db, string orderNumber) {
        var order = AnOrder(orderNumber);

        db.Orders.Add(order);
        db.RecordPlaced(order, LifecycleActor.Buyer(Buyer));
    }

    private static async Task PlaceAsync(OrderDbContext db, string orderNumber) {
        Place(db, orderNumber);
        await db.SaveChangesAsync();
    }

    private static OrderGrpcServerService ServerOver(OrderDbContext db) =>
        new(
            new OrderApplicationService(
                db,
                new Mock<ICartService>().Object,
                new Mock<IProductDirectory>().Object,
                new Mock<IMerchantStanding>().Object,
                new Mock<IPricingClient>().Object,
                new Mock<IShippingClient>().Object,
                new Mock<IAddressDirectory>().Object,
                new Mock<IAddressGeocoder>().Object,
                null!,
                NullLogger<OrderApplicationService>.Instance
            ),
            new Mock<IFulfillmentPackedHandler>().Object,
            new Mock<IOrderDeliveredHandler>().Object,
            new Mock<IReturnsHandler>().Object
        );

    private static async Task<OrderProto.OrdersChangedSinceResponse> ReadAsync(
        string connectionString,
        OrderProto.OrderCursor after,
        int limit = 500
    ) {
        await using var db = PostgresDatabase.NewDbContext(connectionString);

        return await ServerOver(db).OrdersChangedSince(
            new OrderProto.OrdersChangedSinceRequest { Cursor = after, Limit = limit },
            new TestCallContext("/order.v1.OrderService/OrdersChangedSince")
        );
    }

    private static async Task<OrderProto.OrderCursor> TailAsync(string connectionString) {
        var cursor = new OrderProto.OrderCursor { TransactionId = 0 };

        while (true) {
            var page = await ReadAsync(connectionString, cursor);

            cursor = page.Next;
            if (!page.HasMore) {
                return cursor;
            }
        }
    }

    [Fact]
    public async Task AnOrderWhoseTransactionIsStillOpenHoldsTheFeedBackAndIsThenServedFirst() {
        if (ConnectionString is null) {
            return;
        }

        var tail = await TailAsync(ConnectionString);
        var late = AnOrderNumber("LATE");
        var early = AnOrderNumber("EARLY");

        await using var lateDb = PostgresDatabase.NewDbContext(ConnectionString);
        await using (var lateTransaction = await lateDb.Database.BeginTransactionAsync()) {
            await PlaceAsync(lateDb, late);

            await using (var earlyDb = PostgresDatabase.NewDbContext(ConnectionString)) {
                await PlaceAsync(earlyDb, early);
            }

            var whileOpen = await ReadAsync(ConnectionString, tail);
            Assert.Empty(whileOpen.Upserted);
            Assert.Equal(tail, whileOpen.Next);

            await lateTransaction.CommitAsync();
        }

        var afterCommit = await ReadAsync(ConnectionString, tail);

        Assert.Equal([late, early], afterCommit.Upserted.Select(o => o.OrderNumber));
        Assert.Equal([$"Sepatu {late}"], afterCommit.Upserted[0].LineTitles);
        Assert.False(afterCommit.HasMore);
    }

    [Fact]
    public async Task OneTransactionIsPagedByOrderNumberWithoutLosingAny() {
        if (ConnectionString is null) {
            return;
        }

        var tail = await TailAsync(ConnectionString);
        var batch = AnOrderNumber("BATCH");
        string[] numbers = [$"{batch}-C", $"{batch}-A", $"{batch}-B"];

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            foreach (var number in numbers) {
                Place(db, number);
            }

            await db.SaveChangesAsync();
        }

        var first = await ReadAsync(ConnectionString, tail, limit: 2);
        var second = await ReadAsync(ConnectionString, first.Next, limit: 2);

        Assert.Equal([$"{batch}-A", $"{batch}-B"], first.Upserted.Select(o => o.OrderNumber));
        Assert.True(first.HasMore);
        Assert.Equal([$"{batch}-C"], second.Upserted.Select(o => o.OrderNumber));
        Assert.Equal(first.Next.TransactionId, second.Next.TransactionId);
    }

    [Fact]
    public async Task AnOrderChangedAgainMovesToTheEndOfTheFeed() {
        if (ConnectionString is null) {
            return;
        }

        var tail = await TailAsync(ConnectionString);
        var first = AnOrderNumber("FIRST");
        var second = AnOrderNumber("SECOND");

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            await PlaceAsync(db, first);
            await PlaceAsync(db, second);
        }

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            var order = await db.Orders.SingleAsync(o => o.OrderNumber == first);
            order.RecipientName = "Sarah Wijaya";
            await db.SaveChangesAsync();
        }

        var page = await ReadAsync(ConnectionString, tail);

        Assert.Equal([second, first], page.Upserted.Select(o => o.OrderNumber));
    }
}
