using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class SagalessCheckoutCancellerPostgresTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan BlockedLongEnough = TimeSpan.FromMilliseconds(750);

    public static async Task<string> PlaceSagalessAsync(
        string connectionString,
        string buyer,
        string? idempotencyKey,
        string? fingerprint
    ) {
        var orderNumber = $"ORD-SAGALESS-{Guid.NewGuid():N}"[..32];

        await using var db = PostgresDatabase.NewDbContext(connectionString);
        var order = new OrderEntity {
            OrderNumber = orderNumber,
            CustomerPrincipalId = buyer,
            MerchantPrincipalId = Merchant,
            Status = OrderStatus.PENDING_PAYMENT,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta",
            RecipientName = "Test Buyer",
            RecipientPhone = "081200000000",
            IdempotencyKey = idempotencyKey,
            IdempotencyFingerprint = fingerprint,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.Orders.Add(order);
        db.RecordPlaced(order, LifecycleActor.Buyer(buyer));
        await db.SaveChangesAsync();

        return orderNumber;
    }

    private static SagalessCheckoutCanceller Canceller(OrderDbContext db) =>
        new(db, new OrderRowLock(db), NullLogger<SagalessCheckoutCanceller>.Instance);

    [Fact]
    public async Task ASagalessOrderIsCancelledThroughTheLifecycleLogAndClaimsItsSagaSlot() {
        if (PostgresDatabase.ConnectionString is not { } connectionString) {
            return;
        }

        var orderNumber = await PlaceSagalessAsync(connectionString, Guid.NewGuid().ToString(), null, null);

        await using (var db = PostgresDatabase.NewDbContext(connectionString)) {
            await Canceller(db).CancelAsync(Grace, 1000, CancellationToken.None);
        }

        await using var check = PostgresDatabase.NewDbContext(connectionString);
        Assert.Equal(OrderStatus.CANCELLED, (await check.Orders.SingleAsync(o => o.OrderNumber == orderNumber)).Status);

        var steps = await check.OrderLifecycleEvents
            .Where(e => e.OrderNumber == orderNumber)
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.ToStatus, e.ActorKind })
            .ToListAsync();
        Assert.Equal([OrderStatus.PENDING_PAYMENT, OrderStatus.CANCELLED], steps.Select(e => e.ToStatus!.Value));
        Assert.Equal(LifecycleActorKind.SYSTEM, steps[^1].ActorKind);

        var saga = await check.CheckoutSagas.SingleAsync(s => s.OrderNumber == orderNumber);
        Assert.Equal(SagaState.Compensated, saga.State);
        Assert.False(await check.CheckoutSagaSteps.AnyAsync(s => s.SagaId == saga.Id));
    }

    [Fact]
    public async Task ACheckoutThatStartsItsSagaWhileTheOrderIsBeingCancelledKeepsIt() {
        if (PostgresDatabase.ConnectionString is not { } connectionString) {
            return;
        }

        var buyer = Guid.NewGuid().ToString();
        var orderNumber = await PlaceSagalessAsync(connectionString, buyer, null, null);

        await using var late = PostgresDatabase.NewDbContext(connectionString);
        await using var lateTransaction = await late.Database.BeginTransactionAsync();
        late.CheckoutSagas.Add(new CheckoutSaga {
            OrderNumber = orderNumber,
            CustomerPrincipalId = buyer,
            State = SagaState.Running,
            CorrelationId = "late-checkout",
        });
        await late.SaveChangesAsync();

        await using var db = PostgresDatabase.NewDbContext(connectionString);
        var cancelling = Task.Run(() => Canceller(db).CancelAsync(Grace, 1000, CancellationToken.None));

        await Task.Delay(BlockedLongEnough);
        Assert.False(cancelling.IsCompleted, "the cancel should wait on the saga the late checkout is inserting");

        await lateTransaction.CommitAsync();
        await cancelling;

        await using var check = PostgresDatabase.NewDbContext(connectionString);
        Assert.Equal(
            OrderStatus.PENDING_PAYMENT,
            (await check.Orders.SingleAsync(o => o.OrderNumber == orderNumber)).Status
        );

        var saga = await check.CheckoutSagas.SingleAsync(s => s.OrderNumber == orderNumber);
        Assert.Equal("late-checkout", saga.CorrelationId);
        Assert.Equal(1, await check.OrderLifecycleEvents.CountAsync(e => e.OrderNumber == orderNumber));
    }
}
