using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class CheckoutSagaRunnerTests {

    private const string Customer = "f59fd296-a50f-4a32-970f-a1d1fddd76ae";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CheckoutPlan PlanWith(string? voucher, params string[] skus) => new(
        OrderNumber: "ORD-TEST-0001",
        CustomerPrincipalId: Customer,
        VoucherCode: voucher,
        Reservations: [.. skus.Select(sku => new SagaReservation(Merchant, sku, 1))],
        MerchantPrincipalId: Merchant,
        TotalOrderAmount: 120000m,
        MerchantAmount: 100000m,
        ShippingFeeAmount: 20000m);

    private static (Mock<IVoucherQuotaClient>, Mock<IStockClient>, Mock<IEscrowClient>) AllAgreeing() {
        var voucher = new Mock<IVoucherQuotaClient>();
        voucher.Setup(c => c.RedeemVoucherAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());
        voucher.Setup(c => c.ReleaseVoucherAsync(It.IsAny<string>(), It.IsAny<string>()))
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

        return (voucher, stock, escrow);
    }

    private static CheckoutSagaRunner Runner(
        OrderDbContext db, Mock<IVoucherQuotaClient> v, Mock<IStockClient> s, Mock<IEscrowClient> e) =>
        new(db, v.Object, s.Object, e.Object, NullLogger<CheckoutSagaRunner>.Instance);

    [Fact]
    public async Task EverySucceedingStepLeavesTheSagaCompleted() {
        using var db = NewDbContext();
        var (voucher, stock, escrow) = AllAgreeing();

        var outcome = await Runner(db, voucher, stock, escrow).RunAsync(PlanWith("SAVE10", "SKU-1", "SKU-2"));

        Assert.True(outcome.Succeeded);
        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Completed, saga.State);
        Assert.All(await db.CheckoutSagaSteps.ToListAsync(), s => Assert.Equal(SagaStepState.Done, s.State));

        voucher.Verify(c => c.ReleaseVoucherAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        stock.Verify(c => c.ReleaseStockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        escrow.Verify(c => c.RefundHoldAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ARefusedStepGivesBackEverythingTakenBeforeIt() {
        using var db = NewDbContext();
        var (voucher, stock, escrow) = AllAgreeing();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));

        var outcome = await Runner(db, voucher, stock, escrow).RunAsync(PlanWith("SAVE10", "SKU-1", "SKU-2"));

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
        var (voucher, stock, escrow) = AllAgreeing();

        escrow.Setup(c => c.CreateHoldAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new InvalidOperationException("payment is unreachable"));

        var outcome = await Runner(db, voucher, stock, escrow).RunAsync(PlanWith("SAVE10", "SKU-1"));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SagaState.Compensated, (await db.CheckoutSagas.SingleAsync()).State);
        voucher.Verify(c => c.ReleaseVoucherAsync("SAVE10", "ORD-TEST-0001"), Times.Once);
        stock.Verify(c => c.ReleaseStockAsync(Merchant, "SKU-1", 1, "ORD-TEST-0001"), Times.Once);

        escrow.Verify(c => c.RefundHoldAsync("ORD-TEST-0001", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AFailedCompensationLeavesTheSagaStuckRatherThanClaimingItUnwound() {
        using var db = NewDbContext();
        var (voucher, stock, escrow) = AllAgreeing();

        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-2", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("no stock for SKU-2"));
        stock.Setup(c => c.ReleaseStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Refused("warehouse refused the release"));

        var outcome = await Runner(db, voucher, stock, escrow).RunAsync(PlanWith("SAVE10", "SKU-1", "SKU-2"));

        Assert.False(outcome.Succeeded);
        var saga = await db.CheckoutSagas.SingleAsync();

        Assert.Equal(SagaState.Stuck, saga.State);

        voucher.Verify(c => c.ReleaseVoucherAsync("SAVE10", "ORD-TEST-0001"), Times.Once);
    }

    [Fact]
    public async Task ACheckoutWithoutAVoucherNeverAsksPricingAboutOne() {
        using var db = NewDbContext();
        var (voucher, stock, escrow) = AllAgreeing();

        var outcome = await Runner(db, voucher, stock, escrow).RunAsync(PlanWith(null, "SKU-1"));

        Assert.True(outcome.Succeeded);
        voucher.Verify(c => c.RedeemVoucherAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.DoesNotContain(await db.CheckoutSagaSteps.ToListAsync(), s => s.Name == SagaStepName.RedeemVoucher);
    }

    [Fact]
    public async Task EachStepIsRecordedBeforeItIsAttempted() {
        using var db = NewDbContext();
        var (voucher, stock, escrow) = AllAgreeing();

        var stepExistedDuringTheCall = false;
        stock.Setup(c => c.ReserveStockAsync(It.IsAny<string>(), "SKU-1", It.IsAny<int>(), It.IsAny<string>()))
            .Callback(() => stepExistedDuringTheCall =
                db.CheckoutSagaSteps.Any(s => s.Reference == "SKU-1" && s.State == SagaStepState.Attempting))
            .ReturnsAsync(StepResult.Ok());

        await Runner(db, voucher, stock, escrow).RunAsync(PlanWith(null, "SKU-1"));

        Assert.True(stepExistedDuringTheCall);
    }
}
