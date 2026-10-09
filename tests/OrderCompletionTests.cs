using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class OrderCompletionTests {
    private const string OrderNumber = "ORD-COMPLETE-1";

    private static readonly ReturnWindow SevenDays = new(7);

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options
        );

    private static Mock<IEscrowClient> EscrowReleasing() {
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.ReleaseHoldAsync(It.IsAny<string>())).ReturnsAsync(StepResult.Ok());
        return escrow;
    }

    private static OrderCompletion NewCompletion(
        OrderDbContext db, IEscrowClient escrow, UnlockedOrderRows? rows = null
    ) => new(db, rows ?? new UnlockedOrderRows(), escrow, SevenDays, NullLogger<OrderCompletion>.Instance);

    private static async Task<OrderEntity> SeedOrder(
        OrderDbContext db, OrderStatus status = OrderStatus.DELIVERED, double deliveredDaysAgo = 8
    ) {
        var order = new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7",
            MerchantPrincipalId = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
            Status = status,
            DeliveredAt = DateTime.UtcNow.AddDays(-deliveredDaysAgo),
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task SeedReturn(OrderDbContext db, ReturnStatus status) {
        db.OrderReturns.Add(new OrderReturn {
            ReturnNumber = "RMA-1",
            OrderNumber = OrderNumber,
            MerchantPrincipalId = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
            Reason = "Wrong size",
            Status = status,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnOrderPastItsReturnWindowCompletesAndPaysTheMerchant() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = EscrowReleasing();
        var rows = new UnlockedOrderRows();

        var outcome = await NewCompletion(db, escrow.Object, rows)
            .CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);

        Assert.Equal(CompletionStatus.Completed, outcome.Status);
        Assert.True(outcome.EscrowReleased);
        var order = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.COMPLETED, order.Status);
        Assert.NotNull(order.CompletedAt);
        var release = await db.EscrowReleases.SingleAsync();
        Assert.NotNull(release.ReleasedAt);
        Assert.Equal(1, release.Attempts);
        Assert.Equal(1, rows.Locks);
        escrow.Verify(e => e.ReleaseHoldAsync(OrderNumber), Times.Once);
    }

    [Fact]
    public async Task AnOrderInsideItsReturnWindowIsNotCompletedBySweeping() {
        using var db = NewDbContext();
        await SeedOrder(db, deliveredDaysAgo: 6.9);
        var escrow = EscrowReleasing();

        var outcome = await NewCompletion(db, escrow.Object)
            .CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);

        Assert.Equal(CompletionStatus.WindowStillOpen, outcome.Status);
        Assert.Equal(OrderStatus.DELIVERED, (await db.Orders.SingleAsync()).Status);
        Assert.Empty(db.EscrowReleases);
        escrow.Verify(e => e.ReleaseHoldAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnOrderWithNoRecordedDeliveryTimeIsNotCompletedBySweeping() {
        using var db = NewDbContext();
        var order = await SeedOrder(db);
        order.DeliveredAt = null;
        await db.SaveChangesAsync();

        var outcome = await NewCompletion(db, EscrowReleasing().Object)
            .CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);

        Assert.Equal(CompletionStatus.WindowStillOpen, outcome.Status);
    }

    [Fact]
    public async Task AnAdminCanCompleteADeliveredOrderBeforeTheWindowCloses() {
        using var db = NewDbContext();
        await SeedOrder(db, deliveredDaysAgo: 1);

        var outcome = await NewCompletion(db, EscrowReleasing().Object)
            .CompleteAsync(OrderNumber, waitForReturnWindow: false, CancellationToken.None);

        Assert.Equal(CompletionStatus.Completed, outcome.Status);
        Assert.Equal(OrderStatus.COMPLETED, (await db.Orders.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(OrderStatus.PAID)]
    [InlineData(OrderStatus.PROCESSING_FULFILLMENT)]
    [InlineData(OrderStatus.SHIPPED)]
    [InlineData(OrderStatus.CANCELLED)]
    [InlineData(OrderStatus.REFUNDED)]
    public async Task NoOneCompletesAnOrderThatWasNotDelivered(OrderStatus status) {
        using var db = NewDbContext();
        await SeedOrder(db, status);
        var escrow = EscrowReleasing();

        var outcome = await NewCompletion(db, escrow.Object)
            .CompleteAsync(OrderNumber, waitForReturnWindow: false, CancellationToken.None);

        Assert.Equal(CompletionStatus.NotDelivered, outcome.Status);
        Assert.Equal(status, (await db.Orders.SingleAsync()).Status);
        escrow.Verify(e => e.ReleaseHoldAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(ReturnStatus.OPEN)]
    [InlineData(ReturnStatus.GOODS_RECEIVED)]
    public async Task AnUnresolvedReturnKeepsTheEscrowHeld(ReturnStatus status) {
        using var db = NewDbContext();
        await SeedOrder(db);
        await SeedReturn(db, status);
        var escrow = EscrowReleasing();

        var outcome = await NewCompletion(db, escrow.Object)
            .CompleteAsync(OrderNumber, waitForReturnWindow: false, CancellationToken.None);

        Assert.Equal(CompletionStatus.ReturnUnresolved, outcome.Status);
        Assert.Equal(OrderStatus.DELIVERED, (await db.Orders.SingleAsync()).Status);
        escrow.Verify(e => e.ReleaseHoldAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(ReturnStatus.RESOLVED)]
    [InlineData(ReturnStatus.REJECTED)]
    public async Task ASettledReturnNoLongerHoldsTheOrderOpen(ReturnStatus status) {
        using var db = NewDbContext();
        await SeedOrder(db);
        await SeedReturn(db, status);

        var outcome = await NewCompletion(db, EscrowReleasing().Object)
            .CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);

        Assert.Equal(CompletionStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task ACompletedOrderIsNotCompletedTwice() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = EscrowReleasing();
        var completion = NewCompletion(db, escrow.Object);

        await completion.CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);
        var again = await completion.CompleteAsync(OrderNumber, waitForReturnWindow: false, CancellationToken.None);

        Assert.Equal(CompletionStatus.AlreadyCompleted, again.Status);
        Assert.Single(db.EscrowReleases);
        escrow.Verify(e => e.ReleaseHoldAsync(OrderNumber), Times.Once);
    }

    [Fact]
    public async Task AReleasePaymentCouldNotTakeIsOwedAndRetried() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = new Mock<IEscrowClient>();
        escrow.SetupSequence(e => e.ReleaseHoldAsync(OrderNumber))
            .ThrowsAsync(new RpcException(new Status(StatusCode.Unavailable, "payment is restarting")))
            .ReturnsAsync(StepResult.Ok());
        var completion = NewCompletion(db, escrow.Object);

        var outcome = await completion.CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);

        Assert.Equal(CompletionStatus.Completed, outcome.Status);
        Assert.False(outcome.EscrowReleased);
        var release = await db.EscrowReleases.SingleAsync();
        Assert.Null(release.ReleasedAt);
        Assert.True(release.NextAttemptAt > DateTime.UtcNow);
        Assert.Contains("restarting", release.LastError, StringComparison.Ordinal);

        Assert.True(await completion.TryReleaseAsync(release, CancellationToken.None));
        Assert.NotNull(release.ReleasedAt);
        Assert.Null(release.NextAttemptAt);
        Assert.Equal(2, release.Attempts);
    }

    [Fact]
    public async Task AReleasePaymentRefusesIsNotRetried() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.ReleaseHoldAsync(OrderNumber))
            .ThrowsAsync(new RpcException(new Status(StatusCode.FailedPrecondition, "escrow is REFUNDED")));

        await NewCompletion(db, escrow.Object)
            .CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);

        var release = await db.EscrowReleases.SingleAsync();
        Assert.Null(release.ReleasedAt);
        Assert.Null(release.NextAttemptAt);
        Assert.Equal("escrow is REFUNDED", release.LastError);
    }

    [Fact]
    public async Task AnUnknownOrderIsSaidToBeUnknown() {
        using var db = NewDbContext();

        var outcome = await NewCompletion(db, EscrowReleasing().Object)
            .CompleteAsync("ORD-NOBODY", waitForReturnWindow: false, CancellationToken.None);

        Assert.Equal(CompletionStatus.NoSuchOrder, outcome.Status);
    }
}
