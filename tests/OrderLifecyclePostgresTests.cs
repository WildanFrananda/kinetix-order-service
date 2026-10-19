using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Grpc;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class OrderLifecyclePostgresTests {
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";
    private const string MigrationBeforeTheLog = "20261011075410_OrdersOweACourierDispatch";

    private static string? ConnectionString => PostgresDatabase.ConnectionString;

    private static string AnOrderNumber(string label) => $"ORD-PG-{label}-{Guid.NewGuid().ToString("N")[..8]}";

    private static OrderEntity AnOrder(string orderNumber, OrderStatus status = OrderStatus.PENDING_PAYMENT) =>
        new() {
            OrderNumber = orderNumber,
            CustomerPrincipalId = Buyer,
            MerchantPrincipalId = Merchant,
            Status = status,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
        };

    private static async Task<OrderEntity> PlaceAsync(OrderDbContext db, string orderNumber) {
        var order = AnOrder(orderNumber);

        db.Orders.Add(order);
        db.RecordPlaced(order, LifecycleActor.Buyer(Buyer));
        await db.SaveChangesAsync();

        return order;
    }

    private static async Task<Common.V1.LogCursor> TailAsync(string connectionString) {
        await using var db = PostgresDatabase.NewDbContext(connectionString);
        var server = new OrderLifecycleGrpcService(new OrderLifecycleFeed(db));
        var cursor = new Common.V1.LogCursor();

        while (true) {
            var page = await server.LifecycleEventsSince(
                new OrderProto.LifecycleEventsSinceRequest { After = cursor, Limit = 500 },
                new TestCallContext("/order.v1.OrderLifecycleService/LifecycleEventsSince")
            );

            cursor = page.Next;
            if (!page.HasMore) {
                return cursor;
            }
        }
    }

    private static async Task<OrderProto.LifecycleEventsSinceResponse> ReadAsync(
        string connectionString, Common.V1.LogCursor after, int limit = 500
    ) {
        await using var db = PostgresDatabase.NewDbContext(connectionString);

        return await new OrderLifecycleGrpcService(new OrderLifecycleFeed(db)).LifecycleEventsSince(
            new OrderProto.LifecycleEventsSinceRequest { After = after, Limit = limit },
            new TestCallContext("/order.v1.OrderLifecycleService/LifecycleEventsSince")
        );
    }

    [Fact]
    public async Task TheFeedHoldsBackEverythingBehindATransactionStillOpenAndThenServesItAll() {
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
            Assert.Empty(whileOpen.Events);
            Assert.Equal(tail, whileOpen.Next);

            await lateTransaction.CommitAsync();
        }

        var afterCommit = await ReadAsync(ConnectionString, tail);

        Assert.Equal([late, early], afterCommit.Events.Select(e => e.OrderNumber));
        Assert.False(afterCommit.HasMore);
    }

    [Fact]
    public async Task TheFeedPagesInOrderAndHandsBackWhereItStopped() {
        if (ConnectionString is null) {
            return;
        }

        var tail = await TailAsync(ConnectionString);
        var orderNumber = AnOrderNumber("PAGES");
        var at = new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Utc);

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            var order = await PlaceAsync(db, orderNumber);

            db.ChangeStatus(order, OrderStatus.PAID, LifecycleActor.Buyer(Buyer), at);
            await db.SaveChangesAsync();

            db.ChangeStatus(
                order,
                OrderStatus.PROCESSING_FULFILLMENT,
                LifecycleActor.Merchant(Merchant),
                at.AddMinutes(5)
            );
            await db.SaveChangesAsync();
        }

        var first = await ReadAsync(ConnectionString, tail, limit: 2);
        var second = await ReadAsync(ConnectionString, first.Next, limit: 2);
        var nothingNew = await ReadAsync(ConnectionString, second.Next, limit: 2);

        Assert.True(first.HasMore);
        Assert.Equal(2, first.Events.Count);
        Assert.Equal(first.Events[^1].Position, first.Next);
        Assert.False(second.HasMore);
        Assert.Empty(nothingNew.Events);
        Assert.Equal(second.Next, nothingNew.Next);

        var all = first.Events.Concat(second.Events).ToList();
        Assert.Equal(
            [
                (Common.V1.OrderStatus.Unspecified, Common.V1.OrderStatus.PendingPayment),
                (Common.V1.OrderStatus.PendingPayment, Common.V1.OrderStatus.Paid),
                (Common.V1.OrderStatus.Paid, Common.V1.OrderStatus.Packing),
            ],
            all.Select(e => (e.FromStatus, e.ToStatus))
        );
        Assert.All(all, e => Assert.Equal(OrderProto.LifecycleEventKind.StatusChanged, e.Kind));

        var packed = all[^1];
        Assert.Equal(orderNumber, packed.OrderNumber);
        Assert.Equal(OrderProto.LifecycleActorKind.Merchant, packed.ActorKind);
        Assert.Equal(Merchant, packed.ActorPrincipalId);
        Assert.Equal(Buyer, packed.BuyerPrincipalId);
        Assert.Equal(Merchant, packed.MerchantPrincipalId);
        Assert.Equal(at.AddMinutes(5), packed.OccurredAt.ToDateTime());
    }

    [Theory]
    [InlineData("UPDATE order_lifecycle_events SET actor_kind = 'ADMIN'")]
    [InlineData("DELETE FROM order_lifecycle_events")]
    [InlineData("TRUNCATE order_lifecycle_events")]
    public async Task TheLogCannotBeRewritten(string statement) {
        if (ConnectionString is null) {
            return;
        }

        await using var db = PostgresDatabase.NewDbContext(ConnectionString);
        await PlaceAsync(db, AnOrderNumber("REWRITE"));
        var before = await db.OrderLifecycleEvents.CountAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(statement));

        Assert.Contains("append-only", refused.MessageText);
        Assert.Equal(before, await db.OrderLifecycleEvents.CountAsync());
    }

    [Fact]
    public async Task AnOrderStatusChangedWithoutAnEventIsRefusedAtCommit() {
        if (ConnectionString is null) {
            return;
        }

        var orderNumber = AnOrderNumber("UNLOGGED");
        await using (var setup = PostgresDatabase.NewDbContext(ConnectionString)) {
            await PlaceAsync(setup, orderNumber);
        }

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            var order = await db.Orders.SingleAsync(o => o.OrderNumber == orderNumber);
            order.Status = OrderStatus.PAID;

            var refused = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Contains("no lifecycle event", Describe(refused));
        }

        await using var check = PostgresDatabase.NewDbContext(ConnectionString);
        Assert.Equal(
            OrderStatus.PENDING_PAYMENT,
            (await check.Orders.AsNoTracking().SingleAsync(o => o.OrderNumber == orderNumber)).Status
        );
    }

    [Fact]
    public async Task AnOrderInsertedWithoutAnEventIsRefusedAtCommit() {
        if (ConnectionString is null) {
            return;
        }

        var orderNumber = AnOrderNumber("SILENT");

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            db.Orders.Add(AnOrder(orderNumber));

            var refused = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Contains("no lifecycle event", Describe(refused));
        }

        await using var check = PostgresDatabase.NewDbContext(ConnectionString);
        Assert.False(await check.Orders.AnyAsync(o => o.OrderNumber == orderNumber));
    }

    [Fact]
    public async Task AReturnOpenedWithoutAnEventIsRefusedAtCommit() {
        if (ConnectionString is null) {
            return;
        }

        var orderNumber = AnOrderNumber("RETURN");
        await using (var setup = PostgresDatabase.NewDbContext(ConnectionString)) {
            await PlaceAsync(setup, orderNumber);
        }

        var returnNumber = $"RMA-PG-{Guid.NewGuid().ToString("N")[..8]}";

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            db.OrderReturns.Add(new OrderReturn {
                ReturnNumber = returnNumber,
                OrderNumber = orderNumber,
                MerchantPrincipalId = Merchant,
                Reason = "Wrong size",
            });

            var refused = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Contains("no lifecycle event", Describe(refused));
        }

        await using var check = PostgresDatabase.NewDbContext(ConnectionString);
        Assert.False(await check.OrderReturns.AnyAsync(r => r.ReturnNumber == returnNumber));
    }

    [Fact]
    public async Task TheMigrationRecordsEveryExistingOrderAndReturnAsABaselineAndCanBeUndone() {
        if (ConnectionString is null) {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var scratch = $"kinetix_order_baseline_{Guid.NewGuid().ToString("N")[..12]}";
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(scratch);

        await AdministerAsync(ConnectionString, "CREATE DATABASE " + quoted);

        builder.Database = scratch;
        var scratchConnection = builder.ConnectionString;

        try {
            await using var db = PostgresDatabase.NewDbContext(scratchConnection);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(MigrationBeforeTheLog);

            var orderNumber = AnOrderNumber("BASELINE");
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO orders (
                    id, order_number, customer_principal_id, merchant_principal_id, status,
                    subtotal, discount_amount, final_total, shipping_service_tier, base_shipping_fee,
                    shipping_discount, final_shipping_fee, distance_km, shipping_quote_basis,
                    shipping_address, recipient_name, recipient_phone, created_at, updated_at
                )
                VALUES (
                    {Guid.NewGuid()}, {orderNumber}, {Buyer}, {Merchant}, 'DELIVERED',
                    0, 0, 0, 'KINETIX_REGULAR', 0,
                    0, 0, 0, 'CLIENT_SUPPLIED',
                    'Jl. Cikini Raya No. 99', 'Sarah', '0812', now(), now()
                )");
            db.OrderReturns.Add(new OrderReturn {
                ReturnNumber = "RMA-BASELINE-1",
                OrderNumber = orderNumber,
                MerchantPrincipalId = Merchant,
                Reason = "Wrong size",
                Status = ReturnStatus.OPEN,
            });
            await db.SaveChangesAsync();

            await migrator.MigrateAsync();

            var baseline = await db.OrderLifecycleEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync();
            Assert.Equal(2, baseline.Count);
            Assert.All(baseline, e => Assert.Equal(LifecycleEventKind.BASELINE, e.Kind));
            Assert.All(baseline, e => Assert.Equal(LifecycleActorKind.SYSTEM, e.ActorKind));
            Assert.All(baseline, e => Assert.Equal(Buyer, e.BuyerPrincipalId));
            Assert.All(baseline, e => Assert.Equal(Merchant, e.MerchantPrincipalId));
            Assert.Equal((OrderStatus?)OrderStatus.DELIVERED, baseline[0].ToStatus);
            Assert.Null(baseline[0].ReturnNumber);
            Assert.Null(baseline[1].ToStatus);
            Assert.Equal("RMA-BASELINE-1", baseline[1].ReturnNumber);
            Assert.Equal((ReturnStatus?)ReturnStatus.OPEN, baseline[1].ReturnStatus);

            await migrator.MigrateAsync(MigrationBeforeTheLog);

            var completed = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE orders SET status = 'COMPLETED' WHERE order_number = {orderNumber}"
            );
            Assert.Equal(1, completed);
        } finally {
            NpgsqlConnection.ClearAllPools();

            await AdministerAsync(ConnectionString, "DROP DATABASE IF EXISTS " + quoted + " WITH (FORCE)");
        }
    }

    private static async Task AdministerAsync(string connectionString, string statement) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(statement, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Describe(Exception error) {
        var text = new System.Text.StringBuilder();

        for (var current = error; current is not null; current = current.InnerException) {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }
}
