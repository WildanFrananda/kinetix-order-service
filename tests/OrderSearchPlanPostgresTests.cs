using System.Security.Cryptography;
using System.Text;
using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Xunit.Abstractions;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class OrderSearchPlanPostgresTests(ITestOutputHelper output) {
    private const int SeededOrders = 20_000;

    private const string SeedOrders = """
        INSERT INTO orders (
            id,
            order_number,
            customer_principal_id,
            merchant_principal_id,
            status,
            subtotal,
            discount_amount,
            final_total,
            shipping_service_tier,
            base_shipping_fee,
            shipping_discount,
            final_shipping_fee,
            distance_km,
            shipping_quote_basis,
            shipping_address,
            recipient_name,
            recipient_phone,
            created_at,
            updated_at
        )
        SELECT
            gen_random_uuid(),
            'ORD-PLAN-' || lpad(n::text, 6, '0'),
            CASE
                WHEN n % 100 = 1 THEN md5('customer-heavy')::uuid::text
                ELSE md5('customer-' || (n % 2000))::uuid::text
            END,
            md5('merchant-' || (n % 200))::uuid::text,
            CASE
                WHEN n % 50 = 0 THEN 'PENDING_PAYMENT'
                WHEN n % 50 < 3 THEN 'PAID'
                WHEN n % 50 < 5 THEN 'PROCESSING_FULFILLMENT'
                WHEN n % 50 < 10 THEN 'DELIVERED'
                WHEN n % 50 < 15 THEN 'CANCELLED'
                ELSE 'COMPLETED'
            END,
            150000,
            0,
            165000,
            'KINETIX_REGULAR',
            15000,
            0,
            15000,
            4.2,
            'MATCHING_QUOTE',
            'Jl. Cikini Raya No. 99',
            'Sarah',
            '081200000099',
            timestamptz '2150-01-01 00:00:00+00' + n * interval '17 minutes',
            timestamptz '2150-01-01 00:00:00+00' + n * interval '17 minutes'
        FROM generate_series(1, 20000) AS n
        """;

    private const string SeedItems = """
        INSERT INTO order_items (id, order_id, product_id, product_title, unit_price, quantity, line_subtotal)
        SELECT gen_random_uuid(), o.id, 'SKU-' || k, 'Product ' || k, 50000, 1, 50000
        FROM orders o, generate_series(1, 2) AS k
        WHERE o.order_number LIKE 'ORD-PLAN-%'
        """;

    private const string ItemsByOrder = "\"IX_order_items_order_id\"";

    private static readonly string Merchant = Md5Uuid("merchant-7");
    private static readonly string Customer = Md5Uuid("customer-7");
    private static readonly string FrequentCustomer = Md5Uuid("customer-heavy");

    private static string? ConnectionString => PostgresDatabase.ConnectionString;

    private static string Md5Uuid(string text) {
        var hex = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

        return Guid.ParseExact(hex, "N").ToString("D");
    }

    private async Task<string> PlanAsync(ScratchTransaction scratch, string queryString) {
        var parsing = OrderSearchParameters.Parse(QueryHelpers.ParseQuery(queryString));
        Assert.Null(parsing.Refusal);

        var commands = new CapturedCommands();
        await using (var db = await scratch.ContextAsync(commands)) {
            await new OrderSearch(new OrderSearchRepository(db)).SearchAsync(parsing.Query!, CancellationToken.None);
        }

        var search = Assert.Single(commands.Commands);
        await using var explain = scratch.Command("EXPLAIN " + search.CommandText);
        foreach (NpgsqlParameter parameter in search.Parameters) {
            explain.Parameters.Add(parameter.Clone());
        }

        var lines = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync()) {
            while (await reader.ReadAsync()) {
                lines.Add(reader.GetString(0));
            }
        }

        var plan = string.Join("\n", lines);
        output.WriteLine($"GET /api/v1/orders/search{queryString}\n{search.CommandText}\n{plan}\n");

        return plan;
    }

    private static async Task<ScratchTransaction> SeededAsync(string connectionString) {
        var scratch = await ScratchTransaction.BeginAsync(connectionString);
        await scratch.ExecuteAsync(SeedOrders);
        await scratch.ExecuteAsync(SeedItems);
        await scratch.ExecuteAsync("ANALYZE orders");
        await scratch.ExecuteAsync("ANALYZE order_items");

        return scratch;
    }

    private static void Uses(string index, string plan) {
        Assert.True(
            plan.Contains($"using {index} on orders", StringComparison.Ordinal)
                || plan.Contains($"Bitmap Index Scan on {index}", StringComparison.Ordinal),
            $"the plan does not read {index}:\n{plan}"
        );
        Assert.Contains(ItemsByOrder, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
    }

    private static void RangeOf(string column, string plan) {
        Assert.Contains($"Index Cond: ({column} = ", plan, StringComparison.Ordinal);
        Assert.Contains(ItemsByOrder, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
    }

    private static void ServedBy(string index, string plan) {
        Uses(index, plan);
        Assert.DoesNotContain("Sort", plan, StringComparison.Ordinal);
    }

    private static void ServedByEither(string first, string second, string plan) {
        var readsFirst = plan.Contains($"using {first} on orders", StringComparison.Ordinal);
        var readsSecond = plan.Contains($"using {second} on orders", StringComparison.Ordinal);
        Assert.True(readsFirst || readsSecond, $"the plan reads neither {first} nor {second}:\n{plan}");
        Assert.Contains(ItemsByOrder, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachFilterIsARangeOfItsIndexReadNewestFirstWithoutASort() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await SeededAsync(ConnectionString);
        await using (var count = scratch.Command("SELECT count(*) FROM orders WHERE order_number LIKE 'ORD-PLAN-%'")) {
            Assert.Equal((long)SeededOrders, await count.ExecuteScalarAsync());
        }

        var deep = new OrderSearchCursor(
            new DateTime(2150, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(17 * 10_000),
            Guid.Parse("80000000-0000-0000-0000-000000000000")
        ).Encode();

        ServedBy(OrderDbContext.NewestFirstIndex, await PlanAsync(scratch, ""));
        ServedBy(OrderDbContext.NewestFirstIndex, await PlanAsync(scratch, $"?before={deep}"));
        ServedBy(
            OrderDbContext.NewestFirstIndex,
            await PlanAsync(scratch, "?createdFrom=2150-03-01T00:00:00%2B07:00&createdTo=2150-03-08T00:00:00%2B07:00")
        );
        ServedBy(OrderDbContext.StatusNewestFirstIndex, await PlanAsync(scratch, "?status=PAID"));
        ServedByEither(
            OrderDbContext.StatusNewestFirstIndex,
            OrderDbContext.NewestFirstIndex,
            await PlanAsync(scratch, $"?status=CANCELLED&before={deep}")
        );
        ServedBy(OrderDbContext.MerchantNewestFirstIndex, await PlanAsync(scratch, $"?merchant={Merchant}"));
        Uses(OrderDbContext.MerchantNewestFirstIndex, await PlanAsync(scratch, $"?merchant={Merchant}&before={deep}"));
        ServedBy(OrderDbContext.CustomerNewestFirstIndex, await PlanAsync(scratch, $"?customer={FrequentCustomer}"));
        RangeOf("customer_principal_id", await PlanAsync(scratch, $"?customer={Customer}"));
        Uses("\"IX_orders_order_number\"", await PlanAsync(scratch, "?orderNumber=ORD-PLAN-000777"));
    }

    [Fact]
    public async Task CombinedFiltersAndStatusSetsNeverScanTheTable() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await SeededAsync(ConnectionString);

        foreach (var queryString in new[] {
            "?status=PAID,PROCESSING_FULFILLMENT",
            "?status=PENDING_PAYMENT,PAID,PROCESSING_FULFILLMENT,DELIVERED",
            $"?merchant={Merchant}&status=COMPLETED&createdFrom=2150-01-01T00:00:00Z&createdTo=2150-06-01T00:00:00Z",
            $"?customer={Customer}&status=DELIVERED,COMPLETED",
        }) {
            var plan = await PlanAsync(scratch, queryString);

            Assert.DoesNotContain("Seq Scan on orders", plan, StringComparison.Ordinal);
        }
    }
}
