using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class CompletedCheckoutFinisherPostgresTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static async Task<string> SeedAsync(
        string connectionString,
        TimeSpan sagaAge,
        Func<Guid, string, List<CheckoutSagaStep>> steps
    ) {
        var buyer = Guid.NewGuid().ToString();
        var orderNumber = $"ORD-FINISH-{Guid.NewGuid():N}"[..30];

        await using var setup = PostgresDatabase.NewDbContext(connectionString);
        var order = new OrderEntity {
            OrderNumber = orderNumber,
            CustomerPrincipalId = buyer,
            MerchantPrincipalId = Merchant,
            Status = OrderStatus.PENDING_PAYMENT,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta",
            RecipientName = "Test Buyer",
            RecipientPhone = "081200000000",
            CreatedAt = DateTime.UtcNow - sagaAge,
        };
        setup.Orders.Add(order);
        setup.RecordPlaced(order, LifecycleActor.Buyer(buyer));

        var saga = new CheckoutSaga {
            OrderNumber = orderNumber,
            CustomerPrincipalId = buyer,
            State = SagaState.Completed,
            UpdatedAt = DateTime.UtcNow - sagaAge,
        };
        setup.CheckoutSagas.Add(saga);
        setup.CheckoutSagaSteps.AddRange(steps(saga.Id, orderNumber));
        await setup.SaveChangesAsync();

        return orderNumber;
    }

    private static async Task FinishAsync(string connectionString) {
        await using var db = PostgresDatabase.NewDbContext(connectionString);
        var finisher = new CompletedCheckoutFinisher(
            db,
            new OrderRowLock(db),
            NullLogger<CompletedCheckoutFinisher>.Instance
        );

        await finisher.FinishAsync(Grace, 1000, CancellationToken.None);
    }

    [Fact]
    public async Task AnUnpaidOrderOfACompletedSagaIsPaidUnderTheLifecycleLogsTriggers() {
        if (PostgresDatabase.ConnectionString is not { } connectionString) {
            return;
        }

        var orderNumber = await SeedAsync(
            connectionString,
            TimeSpan.FromMinutes(10),
            (sagaId, number) => CompletedSagaShapes.Current(sagaId, number)
        );

        await FinishAsync(connectionString);

        await using var check = PostgresDatabase.NewDbContext(connectionString);
        Assert.Equal(OrderStatus.PAID, (await check.Orders.SingleAsync(o => o.OrderNumber == orderNumber)).Status);
        Assert.Equal(
            [OrderStatus.PENDING_PAYMENT, OrderStatus.PAID],
            await check.OrderLifecycleEvents
                .Where(e => e.OrderNumber == orderNumber)
                .OrderBy(e => e.Sequence)
                .Select(e => e.ToStatus!.Value)
                .ToListAsync()
        );
    }

    [Fact]
    public async Task ASagaCompletedBeforeFulfillmentExistedWaitsForAPersonAndNothingElseTouchesIt() {
        if (PostgresDatabase.ConnectionString is not { } connectionString) {
            return;
        }

        var orderNumber = await SeedAsync(
            connectionString,
            TimeSpan.FromDays(34),
            CompletedSagaShapes.BeforeFulfillment
        );

        await FinishAsync(connectionString);

        DateTime? flaggedAt;
        await using (var first = PostgresDatabase.NewDbContext(connectionString)) {
            var saga = await first.CheckoutSagas.SingleAsync(s => s.OrderNumber == orderNumber);
            flaggedAt = saga.NeedsAttentionAt;

            Assert.NotNull(flaggedAt);
            Assert.Equal(SagaState.Completed, saga.State);
            Assert.Equal(CompensationFailureCode.CompletedWithoutFulfillment, saga.LastFailureCode);
        }

        await FinishAsync(connectionString);

        var policy = CompensationPolicies.Standard();
        await using (var sweep = PostgresDatabase.NewDbContext(connectionString)) {
            var canceller = new SagalessCheckoutCanceller(
                sweep,
                new OrderRowLock(sweep),
                NullLogger<SagalessCheckoutCanceller>.Instance
            );
            await canceller.CancelAsync(TimeSpan.FromMinutes(15), 1000, CancellationToken.None);

            var leases = new SagaLeaseStore(
                sweep,
                new SagaWorkerIdentity(),
                policy,
                NullLogger<SagaLeaseStore>.Instance
            );
            var due = await leases.DueForSweepAsync(1000, 0, CancellationToken.None);
            var saga = await sweep.CheckoutSagas.AsNoTracking().SingleAsync(s => s.OrderNumber == orderNumber);

            Assert.DoesNotContain(saga.Id, due);
        }

        await using var check = PostgresDatabase.NewDbContext(connectionString);
        Assert.Equal(
            OrderStatus.PENDING_PAYMENT,
            (await check.Orders.SingleAsync(o => o.OrderNumber == orderNumber)).Status
        );
        Assert.Equal(1, await check.OrderLifecycleEvents.CountAsync(e => e.OrderNumber == orderNumber));
        Assert.Equal(1, await check.CheckoutSagas.CountAsync(s => s.OrderNumber == orderNumber));
        Assert.Equal(
            flaggedAt,
            (await check.CheckoutSagas.SingleAsync(s => s.OrderNumber == orderNumber)).NeedsAttentionAt
        );

        var queue = await new SagaAdminController(check, TestAudit.Silent()) {
            ControllerContext = SignedIn.As("0b6f1c7e-2d4a-4e8b-9c3f-5a1d7e9b2c40"),
        }.NeedsAttention(limit: 200);
        var row = Assert.Single(
            (List<SagaNeedingAttentionResponse>)((OkObjectResult)queue.Result!).Value!,
            r => r.OrderNumber == orderNumber
        );

        Assert.Equal("PENDING_PAYMENT", row.OrderStatus);
        Assert.Equal(CompletedCheckoutFinisher.BeforeFulfillmentReason, row.FailureReason);
        Assert.Contains(row.UnreleasedSteps, s => s.Remedy.Contains("RefundEscrow"));
        Assert.Contains(row.UnreleasedSteps, s => s.Remedy.Contains("ReleaseStock"));
    }
}
