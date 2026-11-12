using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class CourierDispatchClaimsPostgresTests {
    private const string Merchant = "8d0f6c1e-4f3a-4b8e-9a51-2f7d3c9e0b14";

    private static string? ConnectionString => PostgresDatabase.ConnectionString;

    private static async Task SeedAsync(OrderDbContext db, string orderNumber, DateTime? nextAttemptAt) {
        await db.CourierDispatches.Where(d => d.OrderNumber == orderNumber).ExecuteDeleteAsync();

        db.CourierDispatches.Add(new CourierDispatch {
            OrderNumber = orderNumber,
            MerchantPrincipalId = Merchant,
            FulfillmentTaskId = "TASK-PG",
            NextAttemptAt = nextAttemptAt,
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task APackedReportAndTheSweeperRacingForOneDispatchLeaveExactlyOneClaim() {
        if (ConnectionString is null) {
            return;
        }

        const string orderNumber = "ORD-PGTEST-DISPATCH-RACE";
        var now = DateTime.UtcNow;
        using var setup = PostgresDatabase.NewDbContext(ConnectionString);
        await setup.Database.MigrateAsync();
        await SeedAsync(setup, orderNumber, now.AddSeconds(-5));

        using var packed = PostgresDatabase.NewDbContext(ConnectionString);
        using var sweeper = PostgresDatabase.NewDbContext(ConnectionString);

        var both = await Task.WhenAll(
            new CourierDispatchClaims(packed).TryClaimAsync(orderNumber, now, CancellationToken.None),
            new CourierDispatchClaims(sweeper).TryClaimAsync(orderNumber, now, CancellationToken.None)
        );

        Assert.Single(both, claimed => claimed);

        var row = await setup.CourierDispatches.AsNoTracking().SingleAsync(d => d.OrderNumber == orderNumber);
        Assert.NotNull(row.NextAttemptAt);
        Assert.True(row.NextAttemptAt > now.Add(CourierDispatchClaims.Lease).AddMilliseconds(-1));
    }

    [Fact]
    public async Task ADispatchThatIsNotDueOrIsFinishedIsNotClaimed() {
        if (ConnectionString is null) {
            return;
        }

        var now = DateTime.UtcNow;
        using var db = PostgresDatabase.NewDbContext(ConnectionString);
        await db.Database.MigrateAsync();
        await SeedAsync(db, "ORD-PGTEST-DISPATCH-LATER", now.AddMinutes(1));
        await SeedAsync(db, "ORD-PGTEST-DISPATCH-DONE", null);

        var claims = new CourierDispatchClaims(db);

        Assert.False(await claims.TryClaimAsync("ORD-PGTEST-DISPATCH-LATER", now, CancellationToken.None));
        Assert.False(await claims.TryClaimAsync("ORD-PGTEST-DISPATCH-DONE", now, CancellationToken.None));
        Assert.False(await claims.TryClaimAsync("ORD-PGTEST-DISPATCH-NONE", now, CancellationToken.None));
    }
}
