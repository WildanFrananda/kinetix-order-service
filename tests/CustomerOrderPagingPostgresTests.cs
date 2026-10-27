using System.Data.Common;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderApplicationService = Kinetix.OrderService.Application.Services.OrderService;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class CustomerOrderPagingPostgresTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly DateTime PlacedAt = new(2026, 10, 27, 3, 0, 0, DateTimeKind.Utc);

    private static string? ConnectionString => PostgresDatabase.ConnectionString;

    [Fact]
    public async Task OrdersPlacedAtTheSameInstantComeBackInOneOrderAcrossPages() {
        if (ConnectionString is null) {
            return;
        }

        var buyer = Guid.NewGuid().ToString();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var inserted = ids.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToList();

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            foreach (var id in inserted) {
                var order = AnOrder(id, buyer);
                db.Orders.Add(order);
                db.RecordPlaced(order, LifecycleActor.Buyer(buyer));
            }

            await db.SaveChangesAsync();
        }

        var pages = new List<Guid>();
        for (var page = 1; page <= inserted.Count; page++) {
            await using var db = PostgresDatabase.NewDbContext(ConnectionString);
            var read = await ServiceOver(db).GetCustomerOrdersAsync(buyer, null, page, 1);
            pages.Add(read.Orders.Single().Id);
        }

        Assert.Equal(Enumerable.Reverse(inserted), pages);
    }

    [Fact]
    public async Task TheBuyersIndexServesNewestFirstWithoutASort() {
        if (ConnectionString is null) {
            return;
        }

        await using var db = PostgresDatabase.NewDbContext(ConnectionString);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, transaction, "SET LOCAL enable_seqscan = off");
        await ExecuteAsync(connection, transaction, "SET LOCAL enable_bitmapscan = off");
        await ExecuteAsync(connection, transaction, "SET LOCAL enable_sort = off");

        var plan = await PlanAsync(
            connection,
            transaction,
            "EXPLAIN SELECT id FROM orders WHERE customer_principal_id = 'buyer' "
          + "ORDER BY created_at DESC, id DESC LIMIT 10 OFFSET 10"
        );

        Assert.Contains(OrderDbContext.CustomerNewestFirstIndex, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort", plan, StringComparison.Ordinal);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql) {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> PlanAsync(DbConnection connection, DbTransaction transaction, string sql) {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            lines.Add(reader.GetString(0));
        }

        return string.Join("\n", lines);
    }

    private static OrderEntity AnOrder(Guid id, string buyer) =>
        new() {
            Id = id,
            OrderNumber = $"ORD-PG-TIE-{id:N}",
            CustomerPrincipalId = buyer,
            MerchantPrincipalId = Merchant,
            Status = OrderStatus.PAID,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
            CreatedAt = PlacedAt,
            UpdatedAt = PlacedAt,
        };

    private static OrderApplicationService ServiceOver(OrderDbContext db) =>
        new(
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
        );
}
