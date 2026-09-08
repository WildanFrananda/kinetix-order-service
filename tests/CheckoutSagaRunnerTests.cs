using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Http;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class CheckoutSagaRunnerTests {
    private const string Customer = "f59fd296-a50f-4a32-970f-a1d1fddd76ae";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options
        );

    private const string FlashSale = "7d2c1b90-4e35-4a1f-9c88-15b0e6a4f302";

    private static CheckoutPlan PlanWith(
        string? voucher, string[] skus, params FlashSaleClaim[] claims
    ) => new(
        OrderNumber: "ORD-TEST-0001",
        CustomerPrincipalId: Customer,
        VoucherCode: voucher,
        Reservations: [.. skus.Select(sku => new SagaReservation(Merchant, sku, 1))],
        FlashSaleClaims: claims,
        MerchantPrincipalId: Merchant,
        TotalOrderAmount: 120000m,
        MerchantAmount: 100000m,
        ShippingFeeAmount: 20000m
    );

    private static (Mock<IVoucherQuotaClient>, Mock<IFlashSaleClient>, Mock<IStockClient>, Mock<IEscrowClient>) AllAgreeing() {
        var voucher = new Mock<IVoucherQuotaClient>();
        voucher.Setup(c => c.RedeemVoucherAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());
        voucher.Setup(c => c.ReleaseVoucherAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var flash = new Mock<IFlashSaleClient>();
        flash.Setup(c => c.AllocateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());
        flash.Setup(c => c.ReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var stock = new Mock<IStockClient>();
        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());
        stock.Setup(c => c.ReleaseStockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ReturnsAsync(StepResult.Ok());
        escrow.Setup(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .ReturnsAsync(new EscrowStanding(true, EscrowStandingStatus.Refunded, 12000000, "IDR"));

        return (voucher, flash, stock, escrow);
    }

    private static CheckoutSagaRunner Runner(
        OrderDbContext db,
        Mock<IVoucherQuotaClient> v,
        Mock<IFlashSaleClient> f,
        Mock<IStockClient> s,
        Mock<IEscrowClient> e,
        CompensationPolicy? policy = null,
        ISagaLeaseStore? leases = null
    ) {
        var effectivePolicy = policy ?? new CompensationPolicy();
        return new CheckoutSagaRunner(
            db, v.Object, f.Object, s.Object, e.Object,
            leases ?? new FakeSagaLeaseStore(db, effectivePolicy),
            effectivePolicy,
            new RequestIdAccessor(new HttpContextAccessor()),
            NullLogger<CheckoutSagaRunner>.Instance
        );
    }

    private static async Task MakeDueAgain(OrderDbContext db) {
        var saga = await db.CheckoutSagas.SingleAsync();
        saga.NextAttemptAt = null;
        saga.LeaseOwner = null;
        saga.LeaseExpiresAt = null;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task EverySucceedingStepLeavesTheSagaCompleted() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        var outcome = await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith("SAVE10", ["SKU-1", "SKU-2"]));

        Assert.True(outcome.Succeeded);
        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Completed, saga.State);
        Assert.All(await db.CheckoutSagaSteps.ToListAsync(), s => Assert.Equal(SagaStepState.Done, s.State));

        voucher.Verify(c => c.ReleaseVoucherAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        stock.Verify(c => c.ReleaseStockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        escrow.Verify(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ACompletedSagaHoldsNoLease() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1"]));

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Null(saga.LeaseOwner);
        Assert.Null(saga.LeaseExpiresAt);
    }

    [Fact]
    public async Task ARefusedStepGivesBackEverythingTakenBeforeIt() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));

        var outcome = await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith("SAVE10", ["SKU-1", "SKU-2"]));

        Assert.False(outcome.Succeeded);
        Assert.Contains("SKU-2", outcome.FailureReason);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Compensated, saga.State);

        voucher.Verify(c => c.ReleaseVoucherAsync("SAVE10", "ORD-TEST-0001"), Times.Once);
        stock.Verify(c => c.ReleaseStockAsync(Merchant, "SKU-1", 1, "ORD-TEST-0001"), Times.Once);

        stock.Verify(c => c.ReleaseStockAsync(Merchant, "SKU-2", 1, "ORD-TEST-0001"), Times.Never);
        Assert.Equal(SagaStepState.Failed,
            (await db.CheckoutSagaSteps.SingleAsync(s => s.Reference == "SKU-2")).State);

        escrow.Verify(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnUnreachableServiceCompensatesTheStepsAlreadyTaken() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new InvalidOperationException("payment is unreachable"));

        var outcome = await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith("SAVE10", ["SKU-1"]));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SagaState.Compensated, (await db.CheckoutSagas.SingleAsync()).State);
        voucher.Verify(c => c.ReleaseVoucherAsync("SAVE10", "ORD-TEST-0001"), Times.Once);
        stock.Verify(c => c.ReleaseStockAsync(Merchant, "SKU-1", 1, "ORD-TEST-0001"), Times.Once);

        escrow.Verify(c => c.RefundHoldAsync("ORD-TEST-0001", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AFailedCompensationLeavesTheSagaStuckRatherThanClaimingItUnwound() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));
        stock.Setup(c => c.ReleaseStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("warehouse refused the release"));

        var outcome = await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith("SAVE10", ["SKU-1", "SKU-2"]));

        Assert.False(outcome.Succeeded);
        var saga = await db.CheckoutSagas.SingleAsync();

        Assert.Equal(SagaState.Stuck, saga.State);
        Assert.Equal(1, saga.CompensationAttempts);
        Assert.NotNull(saga.NextAttemptAt);
        Assert.Null(saga.AbandonedAt);

        voucher.Verify(c => c.ReleaseVoucherAsync("SAVE10", "ORD-TEST-0001"), Times.Once);
    }

    [Fact]
    public async Task EveryCompensationRoundIsCountedEvenWhenItSucceeds() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-1"));

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith("SAVE10", ["SKU-1"]));

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Compensated, saga.State);

        Assert.Equal(1, saga.CompensationAttempts);

        var attempt = await db.CompensationAttempts.SingleAsync();
        Assert.Equal(1, attempt.AttemptNo);
        Assert.Equal(CompensationAttemptOutcome.Released, attempt.Outcome);
        Assert.NotNull(attempt.FinishedAt);
    }

    [Fact]
    public async Task ARetryOnlyReissuesTheStepsStillHeld() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));
        stock.Setup(c => c.ReleaseStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("warehouse refused the release"));

        var runner = Runner(db, voucher, flash, stock, escrow);
        await runner.RunAsync(PlanWith("SAVE10", ["SKU-1", "SKU-2"]));

        await MakeDueAgain(db);
        var saga = await db.CheckoutSagas.SingleAsync();
        await runner.CompensateAsync(saga, "retry", heldLease: null, CancellationToken.None);

        voucher.Verify(c => c.ReleaseVoucherAsync("SAVE10", "ORD-TEST-0001"), Times.Once);
        stock.Verify(c => c.ReleaseStockAsync(Merchant, "SKU-1", 1, "ORD-TEST-0001"), Times.Exactly(2));

        var reloaded = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(2, reloaded.CompensationAttempts);
        Assert.Equal(2, await db.CompensationAttempts.CountAsync());
    }

    [Fact]
    public async Task AStepThatComesBackAlreadyReleasedCountsAsUnwoundAndIsRecordedAsSuch() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));
        stock.Setup(c => c.ReleaseStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Repeat());

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1", "SKU-2"]));

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Compensated, saga.State);

        var step = await db.CheckoutSagaSteps.SingleAsync(s => s.Reference == "SKU-1");
        Assert.Equal(SagaStepState.Compensated, step.State);
        Assert.True(step.CompensatedByRepeat);
    }

    [Fact]
    public async Task TheLastRoundAbandonsTheSagaInsteadOfLeavingItRetryable() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();
        var policy = new CompensationPolicy(maxAttempts: 2);

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));
        stock.Setup(c => c.ReleaseStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("warehouse refused the release"));

        var runner = Runner(db, voucher, flash, stock, escrow, policy);
        await runner.RunAsync(PlanWith(null, ["SKU-1", "SKU-2"]));

        Assert.Equal(SagaState.Stuck, (await db.CheckoutSagas.SingleAsync()).State);

        await MakeDueAgain(db);
        await runner.CompensateAsync(await db.CheckoutSagas.SingleAsync(), "retry", heldLease: null, CancellationToken.None);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Abandoned, saga.State);
        Assert.NotNull(saga.AbandonedAt);
        Assert.NotNull(saga.NeedsAttentionAt);
        Assert.Null(saga.NextAttemptAt);
    }

    [Fact]
    public async Task ASagaWhoseEveryRoundDiedIsAbandonedAtClaimTimeRatherThanParkedForever() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();
        var policy = new CompensationPolicy(maxAttempts: 2);

        var saga = new CheckoutSaga {
            OrderNumber = "ORD-TEST-CRASHED",
            CustomerPrincipalId = Customer,
            State = SagaState.Compensating,
            CompensationAttempts = 2,
        };
        db.CheckoutSagas.Add(saga);
        await db.SaveChangesAsync();

        await Runner(db, voucher, flash, stock, escrow, policy)
            .CompensateAsync(saga, "the checkout never finished", heldLease: null, CancellationToken.None);

        var reloaded = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Abandoned, reloaded.State);
        Assert.Equal(CompensationFailureCode.AttemptsExhausted, reloaded.LastFailureCode);
        Assert.NotNull(reloaded.NeedsAttentionAt);
    }

    [Fact]
    public async Task ALiveCheckoutHoldsALeaseThatASweeperCannotTake() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();
        var policy = new CompensationPolicy();

        Guid sagaId = Guid.Empty;
        var sweeperClaimed = true;

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .Returns(async () => {
                sagaId = (await db.CheckoutSagas.SingleAsync()).Id;
                var sweeper = new FakeSagaLeaseStore(db, policy);
                sweeperClaimed = await sweeper.TryClaimAsync(sagaId, null, CancellationToken.None) is not null;
                return StepResult.Ok();
            });

        var outcome = await Runner(db, voucher, flash, stock, escrow, policy).RunAsync(PlanWith(null, ["SKU-1"]));

        Assert.False(sweeperClaimed);
        Assert.True(outcome.Succeeded);
        Assert.Equal(SagaState.Completed, (await db.CheckoutSagas.SingleAsync()).State);
    }

    [Fact]
    public async Task AStalledCheckoutThatLosesItsLeaseCannotWriteCompletedOverTheUnwind() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();
        var policy = new CompensationPolicy();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .Returns(async () => {
                var saga = await db.CheckoutSagas.SingleAsync();
                saga.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();

                var taker = new FakeSagaLeaseStore(db, policy);
                Assert.NotNull(await taker.TryClaimAsync(saga.Id, null, CancellationToken.None));
                return StepResult.Ok();
            });

        var outcome = await Runner(db, voucher, flash, stock, escrow, policy).RunAsync(PlanWith(null, ["SKU-1"]));

        Assert.False(outcome.Succeeded);

        var reloaded = await db.CheckoutSagas.SingleAsync();
        Assert.NotEqual(SagaState.Completed, reloaded.State);
        Assert.Equal(SagaState.Compensating, reloaded.State);

        var step = await db.CheckoutSagaSteps.SingleAsync();
        Assert.Equal(SagaStepState.Attempting, step.State);
    }

    [Fact]
    public async Task AWorkerThatLosesItsLeaseDuringACallCannotCloseTheStepItWasHolding() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();
        var policy = new CompensationPolicy();

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new TimeoutException("payment did not answer the hold in time"));
        escrow.Setup(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Absent(alreadyDone: true, "payment held nothing to refund for this order"));

        string? takerOwner = null;
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .Returns(async () => {
                var live = await db.CheckoutSagas.SingleAsync();
                live.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();

                var taker = await new FakeSagaLeaseStore(db, policy)
                    .TryClaimAsync(live.Id, null, CancellationToken.None);
                takerOwner = taker?.Owner;
                return new EscrowStanding(false, EscrowStandingStatus.Unspecified, 0, string.Empty);
            });

        await Runner(db, voucher, flash, stock, escrow, policy).RunAsync(PlanWith(null, []));

        Assert.NotNull(takerOwner);

        var step = await db.CheckoutSagaSteps.SingleAsync();
        Assert.Equal(SagaStepState.Attempting, step.State);
        Assert.Null(step.LastFailureCode);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Compensating, saga.State);
        Assert.Equal(takerOwner, saga.LeaseOwner);
        Assert.Null(saga.NeedsAttentionAt);
    }

    [Fact]
    public async Task AWorkerThatLostItsLeaseDoesNotGetToIssueOneMoreRelease() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();
        var policy = new CompensationPolicy();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));

        stock.Setup(c => c.ReleaseStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());

        var runner = Runner(db, voucher, flash, stock, escrow, policy);
        await runner.RunAsync(PlanWith(null, ["SKU-1", "SKU-2"]));

        var saga = await db.CheckoutSagas.SingleAsync();
        saga.State = SagaState.Stuck;
        saga.NextAttemptAt = null;
        saga.LeaseOwner = null;
        saga.LeaseExpiresAt = null;
        await db.SaveChangesAsync();

        var step = await db.CheckoutSagaSteps.SingleAsync(s => s.Reference == "SKU-1");
        step.State = SagaStepState.Done;
        await db.SaveChangesAsync();

        var store = new FakeSagaLeaseStore(db, policy);
        var impostor = new SagaLease(saga.Id, "a-worker-that-never-held-this", 1);

        Assert.False(await store.TryCountStepDispatchAsync(impostor, step.Id, CancellationToken.None));
        Assert.False(await store.TryRecordStepOutcomeAsync(
            impostor, step.Id, SagaStepState.Compensated, false, "zombie", "ZOMBIE",
            CancellationToken.None));

        var reloaded = await db.CheckoutSagaSteps.SingleAsync(s => s.Reference == "SKU-1");
        Assert.Equal(SagaStepState.Done, reloaded.State);
        Assert.Equal(1, reloaded.CompensationAttempts);
        Assert.Null(reloaded.LastFailureCode);
    }

    [Fact]
    public async Task ALaterCleanRoundDoesNotEraseTheCodeThatExplainsTheFlag() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new InvalidOperationException("payment died mid-hold"));
        escrow.Setup(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Absent(alreadyDone: true, "payment held nothing to refund for this order"));
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .ReturnsAsync(new EscrowStanding(false, EscrowStandingStatus.Unspecified, 0, string.Empty));

        stock.SetupSequence(c => c.ReleaseStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("warehouse refused the release"))
            .ReturnsAsync(StepResult.Ok());

        var runner = Runner(db, voucher, flash, stock, escrow);
        await runner.RunAsync(PlanWith(null, ["SKU-1"]));

        var afterFirstRound = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Stuck, afterFirstRound.State);
        Assert.Equal(CompensationFailureCode.EscrowAbsent, afterFirstRound.LastFailureCode);
        Assert.NotNull(afterFirstRound.NeedsAttentionAt);

        await MakeDueAgain(db);
        await runner.CompensateAsync(await db.CheckoutSagas.SingleAsync(), "retry", heldLease: null, CancellationToken.None);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Compensated, saga.State);
        Assert.NotNull(saga.NeedsAttentionAt);
        Assert.Equal(CompensationFailureCode.EscrowAbsent, saga.LastFailureCode);
    }

    [Fact]
    public async Task AnUnwoundSagaLeavesNoOrderSittingAtPendingPayment() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        db.Orders.Add(new OrderEntity {
            OrderNumber = "ORD-TEST-0001",
            CustomerPrincipalId = Customer,
            Status = OrderStatus.PENDING_PAYMENT,
        });
        await db.SaveChangesAsync();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1", "SKU-2"]));

        Assert.Equal(SagaState.Compensated, (await db.CheckoutSagas.SingleAsync()).State);

        var order = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.CANCELLED, order.Status);
    }

    [Fact]
    public async Task ARefundThatNeverAnsweredIsSentAgainUnderTheSameKeyUntilTheBudgetIsSpent() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();
        var policy = new CompensationPolicy(maxAttempts: 3);

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new TimeoutException("payment did not answer the hold in time"));
        escrow.Setup(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new TimeoutException("payment did not answer in time"));
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .ReturnsAsync(new EscrowStanding(true, EscrowStandingStatus.Held, 12000000, "IDR"));

        var runner = Runner(db, voucher, flash, stock, escrow, policy);
        await runner.RunAsync(PlanWith(null, ["SKU-1"]));

        for (var round = 0; round < 2; round++) {
            await MakeDueAgain(db);
            await runner.CompensateAsync(await db.CheckoutSagas.SingleAsync(), "retry", heldLease: null, CancellationToken.None);
        }

        escrow.Verify(c => c.RefundHoldAsync("ORD-TEST-0001", It.IsAny<string>()), Times.Exactly(3));

        var step = await db.CheckoutSagaSteps.SingleAsync(s => s.Name == SagaStepName.CreateEscrowHold);
        Assert.Equal(3, step.CompensationAttempts);
        Assert.NotEqual(SagaStepState.Compensated, step.State);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Abandoned, saga.State);
        Assert.NotNull(saga.NeedsAttentionAt);
        Assert.Equal(CompensationFailureCode.EscrowRefundUnconfirmed, saga.LastFailureCode);
    }

    [Fact]
    public async Task AnAbsentHoldIsRefundedASecondTimeBeforeTheLegIsClosed() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new InvalidOperationException("payment died mid-hold"));

        escrow.SetupSequence(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Absent(alreadyDone: false, "payment held nothing to refund for this order"))
            .ReturnsAsync(StepResult.Absent(alreadyDone: true, "payment held nothing to refund for this order"));
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .ReturnsAsync(new EscrowStanding(false, EscrowStandingStatus.Unspecified, 0, string.Empty));

        var runner = Runner(db, voucher, flash, stock, escrow);
        await runner.RunAsync(PlanWith(null, ["SKU-1"]));

        var afterFirstRound = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Stuck, afterFirstRound.State);
        Assert.Equal(CompensationFailureCode.EscrowAbsent, afterFirstRound.LastFailureCode);
        Assert.NotNull(afterFirstRound.NeedsAttentionAt);

        await MakeDueAgain(db);
        await runner.CompensateAsync(await db.CheckoutSagas.SingleAsync(), "retry", heldLease: null, CancellationToken.None);

        escrow.Verify(c => c.RefundHoldAsync("ORD-TEST-0001", It.IsAny<string>()), Times.Exactly(2));

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Compensated, saga.State);
        Assert.NotNull(saga.NeedsAttentionAt);
        Assert.Equal(CompensationFailureCode.EscrowAbsent, saga.LastFailureCode);
    }

    [Fact]
    public async Task ARefusedHoldIsCheckedAgainstPaymentsLedgerRatherThanAssumedToHaveTakenNothing() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ReturnsAsync(StepResult.Refused("this customer's wallet has not got that much in it"));
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .ReturnsAsync(new EscrowStanding(false, EscrowStandingStatus.Unspecified, 0, string.Empty));

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1"]));

        escrow.Verify(c => c.GetStandingAsync("ORD-TEST-0001"), Times.Once);
        escrow.Verify(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Compensated, saga.State);
        Assert.Null(saga.NeedsAttentionAt);

        var step = await db.CheckoutSagaSteps.SingleAsync(s => s.Name == SagaStepName.CreateEscrowHold);
        Assert.Equal(SagaStepState.Compensated, step.State);
    }

    [Fact]
    public async Task ARefusedHoldPaymentIsHoldingAnywayGoesToAHumanRatherThanARefundOnAGuess() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ReturnsAsync(StepResult.Refused("this customer's wallet has not got that much in it"));
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .ReturnsAsync(new EscrowStanding(true, EscrowStandingStatus.Held, 12000000, "IDR"));

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1"]));

        escrow.Verify(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Abandoned, saga.State);
        Assert.Equal(CompensationFailureCode.EscrowRefusedButHeld, saga.LastFailureCode);
        Assert.NotNull(saga.NeedsAttentionAt);
    }

    [Fact]
    public async Task AHoldOrderRecordedButPaymentDoesNotHaveAbandonsImmediately() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        escrow.Setup(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("payment held nothing to refund for this order"));
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .ReturnsAsync(new EscrowStanding(false, EscrowStandingStatus.Unspecified, 0, string.Empty));

        var runner = Runner(db, voucher, flash, stock, escrow);
        await runner.RunAsync(PlanWith(null, ["SKU-1"]));

        var completed = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Completed, completed.State);

        completed.State = SagaState.Stuck;
        completed.LeaseOwner = null;
        completed.LeaseExpiresAt = null;
        await db.SaveChangesAsync();

        await runner.CompensateAsync(completed, "a later step failed", heldLease: null, CancellationToken.None);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Abandoned, saga.State);
        Assert.Equal(CompensationFailureCode.EscrowMissingAfterHold, saga.LastFailureCode);
        Assert.NotNull(saga.NeedsAttentionAt);
    }

    [Fact]
    public async Task AHoldTheMerchantHasAlreadyBeenPaidAbandonsWithoutSpendingTheBudget() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new TimeoutException("payment did not answer the hold in time"));
        escrow.Setup(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("payment did not answer"));
        escrow.Setup(c => c.GetStandingAsync(It.IsAny<string>()))
            .ReturnsAsync(new EscrowStanding(true, EscrowStandingStatus.Released, 12000000, "IDR"));

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1"]));

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Abandoned, saga.State);
        Assert.Equal(CompensationFailureCode.EscrowAlreadyReleased, saga.LastFailureCode);
        Assert.Equal(1, saga.CompensationAttempts);
    }

    [Fact]
    public async Task ACheckoutWithoutAVoucherNeverAsksPricingAboutOne() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        var outcome = await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1"]));

        Assert.True(outcome.Succeeded);
        voucher.Verify(c => c.RedeemVoucherAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.DoesNotContain(await db.CheckoutSagaSteps.ToListAsync(), s => s.Name == SagaStepName.RedeemVoucher);
    }

    [Fact]
    public async Task FlashSaleStockIsClaimedBeforeTheShelfIsReserved() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        var outcome = await Runner(db, voucher, flash, stock, escrow)
            .RunAsync(PlanWith(null, ["SKU-1"], new FlashSaleClaim(FlashSale, "SKU-1", 2)));

        Assert.True(outcome.Succeeded);
        flash.Verify(c => c.AllocateAsync(FlashSale, "SKU-1", 2, "ORD-TEST-0001"), Times.Once);

        var steps = await db.CheckoutSagaSteps.OrderBy(s => s.CreatedAt).ToListAsync();
        Assert.Equal(SagaStepName.AllocateFlashSaleStock, steps[0].Name);
        Assert.Equal(SagaStepName.ReserveStock, steps[1].Name);
    }

    [Fact]
    public async Task AnExhaustedFlashSaleGivesBackTheVoucherAndTakesNoStock() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        flash.Setup(c => c.AllocateAsync(FlashSale, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("this flash sale has not got that many units left"));

        var outcome = await Runner(db, voucher, flash, stock, escrow)
            .RunAsync(PlanWith("SAVE10", ["SKU-1"], new FlashSaleClaim(FlashSale, "SKU-1", 2)));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SagaState.Compensated, (await db.CheckoutSagas.SingleAsync()).State);

        voucher.Verify(c => c.ReleaseVoucherAsync("SAVE10", "ORD-TEST-0001"), Times.Once);

        stock.Verify(c => c.ReserveStockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AllocatedFlashSaleStockIsReleasedWhenALaterStepRefuses() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-1"));

        var outcome = await Runner(db, voucher, flash, stock, escrow)
            .RunAsync(PlanWith(null, ["SKU-1"], new FlashSaleClaim(FlashSale, "SKU-1", 2)));

        Assert.False(outcome.Succeeded);

        flash.Verify(c => c.ReleaseAsync(FlashSale, "SKU-1", 2, "ORD-TEST-0001"), Times.Once);
    }

    [Fact]
    public async Task EachStepIsRecordedBeforeItIsAttempted() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        var stepExistedDuringTheCall = false;
        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .Callback(() => stepExistedDuringTheCall =
                db.CheckoutSagaSteps.Any(s => s.Reference == "SKU-1" && s.State == SagaStepState.Attempting))
            .ReturnsAsync(StepResult.Ok());

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1"]));

        Assert.True(stepExistedDuringTheCall);
    }

    [Fact]
    public async Task ADispatchedReleaseIsCountedBeforeTheCallGoesOut() {
        using var db = NewDbContext();
        var (voucher, flash, stock, escrow) = AllAgreeing();

        var countedDuringTheCall = 0;
        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));
        stock.Setup(c => c.ReleaseStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .Callback(() => countedDuringTheCall =
                db.CheckoutSagaSteps.Single(s => s.Reference == "SKU-1").CompensationAttempts)
            .ReturnsAsync(StepResult.Ok());

        await Runner(db, voucher, flash, stock, escrow).RunAsync(PlanWith(null, ["SKU-1", "SKU-2"]));

        Assert.Equal(1, countedDuringTheCall);
    }
}
