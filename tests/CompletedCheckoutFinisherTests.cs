using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class CompletedCheckoutFinisherTests {
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";
    private const string OrderNumber = "ORD-FINISH-0001";

    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options
        );

    private static async Task<CheckoutSaga> SeedAsync(
        OrderDbContext db,
        OrderStatus status,
        SagaState state,
        TimeSpan sagaAge,
        Func<Guid, List<CheckoutSagaStep>> steps
    ) {
        db.Orders.Add(new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = Buyer,
            MerchantPrincipalId = Merchant,
            Status = status,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta",
            RecipientName = "Test Buyer",
            RecipientPhone = "081200000000",
        });

        var saga = new CheckoutSaga {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = Buyer,
            State = state,
            UpdatedAt = DateTime.UtcNow - sagaAge,
        };
        db.CheckoutSagas.Add(saga);
        db.CheckoutSagaSteps.AddRange(steps(saga.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return saga;
    }

    private static List<CheckoutSagaStep> CurrentShape(Guid sagaId) => CompletedSagaShapes.Current(sagaId, OrderNumber);

    private static List<CheckoutSagaStep> BeforeFulfillment(Guid sagaId) =>
        CompletedSagaShapes.BeforeFulfillment(sagaId, OrderNumber);

    private static CompletedCheckoutFinisher Finisher(OrderDbContext db) =>
        new(db, new UnlockedOrderRows(), NullLogger<CompletedCheckoutFinisher>.Instance);

    [Fact]
    public async Task ACompletedSagaWhoseOrderWasNeverMarkedPaidIsFinishedAsPaid() {
        using var db = NewDbContext();
        await SeedAsync(db, OrderStatus.PENDING_PAYMENT, SagaState.Completed, TimeSpan.FromMinutes(10), CurrentShape);

        var finished = await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None);

        Assert.Equal(1, finished);
        Assert.Equal(OrderStatus.PAID, (await db.Orders.SingleAsync()).Status);

        var logged = await db.OrderLifecycleEvents.SingleAsync();
        Assert.Equal(OrderStatus.PENDING_PAYMENT, logged.FromStatus);
        Assert.Equal(OrderStatus.PAID, logged.ToStatus);
        Assert.Equal(LifecycleActorKind.SYSTEM, logged.ActorKind);
        Assert.Null((await db.CheckoutSagas.SingleAsync()).NeedsAttentionAt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TheVoucherAndFlashSaleStepsAreOptionalForACompletedCheckout(bool voucher, bool flashSale) {
        using var db = NewDbContext();
        await SeedAsync(
            db,
            OrderStatus.PENDING_PAYMENT,
            SagaState.Completed,
            TimeSpan.FromMinutes(10),
            sagaId => CompletedSagaShapes.Current(sagaId, OrderNumber, voucher, flashSale)
        );

        Assert.Equal(1, await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None));
        Assert.Equal(OrderStatus.PAID, (await db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task ASagaCompletedBeforeFulfillmentExistedIsFlaggedAndItsOrderStaysUnpaid() {
        using var db = NewDbContext();
        await SeedAsync(
            db,
            OrderStatus.PENDING_PAYMENT,
            SagaState.Completed,
            TimeSpan.FromDays(34),
            BeforeFulfillment
        );

        Assert.Equal(0, await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None));

        Assert.Equal(OrderStatus.PENDING_PAYMENT, (await db.Orders.SingleAsync()).Status);
        Assert.Empty(db.OrderLifecycleEvents);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Completed, saga.State);
        Assert.NotNull(saga.NeedsAttentionAt);
        Assert.Equal(CompensationFailureCode.CompletedWithoutFulfillment, saga.LastFailureCode);
        Assert.Equal(CompletedCheckoutFinisher.BeforeFulfillmentReason, saga.FailureReason);
    }

    [Fact]
    public async Task AFlaggedSagaIsLeftAsItIsOnEveryLaterPass() {
        using var db = NewDbContext();
        await SeedAsync(
            db,
            OrderStatus.PENDING_PAYMENT,
            SagaState.Completed,
            TimeSpan.FromDays(34),
            BeforeFulfillment
        );

        await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None);
        db.ChangeTracker.Clear();
        var flaggedAt = (await db.CheckoutSagas.SingleAsync()).NeedsAttentionAt;
        db.ChangeTracker.Clear();

        Assert.Equal(0, await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None));
        Assert.Equal(0, await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Equal(flaggedAt, (await db.CheckoutSagas.SingleAsync()).NeedsAttentionAt);
        Assert.Equal(OrderStatus.PENDING_PAYMENT, (await db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task ACompletedSagaWithAStepThatDidNotFinishIsFlaggedNotPaid() {
        using var db = NewDbContext();
        await SeedAsync(
            db,
            OrderStatus.PENDING_PAYMENT,
            SagaState.Completed,
            TimeSpan.FromMinutes(10),
            sagaId => {
                var steps = CurrentShape(sagaId);
                steps[^1].State = SagaStepState.Attempting;

                return steps;
            }
        );

        Assert.Equal(0, await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None));

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.NotNull(saga.NeedsAttentionAt);
        Assert.Contains("CreateFulfillmentOrder 901 finished (it is Attempting)", saga.FailureReason);
    }

    [Fact]
    public async Task ASagaThatCompletedMomentsAgoIsLeftToTheCheckoutStillAnsweringIt() {
        using var db = NewDbContext();
        await SeedAsync(db, OrderStatus.PENDING_PAYMENT, SagaState.Completed, TimeSpan.FromSeconds(5), CurrentShape);

        Assert.Equal(0, await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None));
        Assert.Equal(OrderStatus.PENDING_PAYMENT, (await db.Orders.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(SagaState.Compensated)]
    [InlineData(SagaState.Stuck)]
    [InlineData(SagaState.Running)]
    [InlineData(SagaState.Abandoned)]
    public async Task ASagaThatDidNotCompleteNeverMakesItsOrderPaid(SagaState state) {
        using var db = NewDbContext();
        await SeedAsync(db, OrderStatus.PENDING_PAYMENT, state, TimeSpan.FromMinutes(10), CurrentShape);

        Assert.Equal(0, await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None));
        Assert.Equal(OrderStatus.PENDING_PAYMENT, (await db.Orders.SingleAsync()).Status);
        Assert.Empty(db.OrderLifecycleEvents);
    }

    [Fact]
    public async Task AnOrderAlreadyPaidIsLeftAlone() {
        using var db = NewDbContext();
        await SeedAsync(db, OrderStatus.PAID, SagaState.Completed, TimeSpan.FromMinutes(10), CurrentShape);

        Assert.Equal(0, await Finisher(db).FinishAsync(Grace, 20, CancellationToken.None));
        Assert.Empty(db.OrderLifecycleEvents);
    }
}
