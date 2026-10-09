using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class ReturnRejectionTests {
    private const string OrderNumber = "ORD-REJECT-1";
    private const string ReturnNumber = "RMA-REJECT-1";

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options
        );

    private static ReturnRejection NewRejection(OrderDbContext db, UnlockedOrderRows? rows = null) =>
        new(db, rows ?? new UnlockedOrderRows(), NullLogger<ReturnRejection>.Instance);

    private static async Task Seed(OrderDbContext db, ReturnStatus status, DateTime? goodsReceivedAt = null) {
        db.Orders.Add(new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7",
            MerchantPrincipalId = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
            Status = OrderStatus.DELIVERED,
            DeliveredAt = DateTime.UtcNow.AddDays(-30),
        });
        db.OrderReturns.Add(new OrderReturn {
            ReturnNumber = ReturnNumber,
            OrderNumber = OrderNumber,
            MerchantPrincipalId = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
            Reason = "Wrong size",
            Status = status,
            GoodsReceivedAt = goodsReceivedAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AReturnWhoseGoodsNeverCameBackIsRejectedWithTheReasonGiven() {
        using var db = NewDbContext();
        await Seed(db, ReturnStatus.OPEN);
        var rows = new UnlockedOrderRows();

        var status = await NewRejection(db, rows)
            .RejectAsync(ReturnNumber, "  nothing came back in 30 days  ", CancellationToken.None);

        Assert.Equal(ReturnRejectionStatus.Rejected, status);
        var stored = await db.OrderReturns.SingleAsync();
        Assert.Equal(ReturnStatus.REJECTED, stored.Status);
        Assert.Equal("nothing came back in 30 days", stored.RejectionReason);
        Assert.NotNull(stored.RejectedAt);
        Assert.Equal(1, rows.Locks);
    }

    [Fact]
    public async Task ARejectedReturnNoLongerKeepsTheMerchantFromBeingPaid() {
        using var db = NewDbContext();
        await Seed(db, ReturnStatus.OPEN);
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.ReleaseHoldAsync(OrderNumber)).ReturnsAsync(StepResult.Ok());
        var completion = new OrderCompletion(db, new UnlockedOrderRows(), escrow.Object, new ReturnWindow(7),
            NullLogger<OrderCompletion>.Instance
        );

        var before = await completion.CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);
        await NewRejection(db).RejectAsync(ReturnNumber, "nothing came back", CancellationToken.None);
        var after = await completion.CompleteAsync(OrderNumber, waitForReturnWindow: true, CancellationToken.None);

        Assert.Equal(CompletionStatus.ReturnUnresolved, before.Status);
        Assert.Equal(CompletionStatus.Completed, after.Status);
        escrow.Verify(e => e.ReleaseHoldAsync(OrderNumber), Times.Once);
    }

    [Theory]
    [InlineData(ReturnStatus.GOODS_RECEIVED)]
    [InlineData(ReturnStatus.RESOLVED)]
    public async Task AReturnWhoseGoodsCameBackCannotBeRejected(ReturnStatus status) {
        using var db = NewDbContext();
        await Seed(db, status, DateTime.UtcNow.AddDays(-1));

        var outcome = await NewRejection(db).RejectAsync(ReturnNumber, "changed my mind", CancellationToken.None);

        Assert.Equal(ReturnRejectionStatus.GoodsAlreadyReceived, outcome);
        Assert.Equal(status, (await db.OrderReturns.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ARejectionMustSayWhy(string reason) {
        using var db = NewDbContext();
        await Seed(db, ReturnStatus.OPEN);

        var outcome = await NewRejection(db).RejectAsync(ReturnNumber, reason, CancellationToken.None);

        Assert.Equal(ReturnRejectionStatus.NoReasonGiven, outcome);
        Assert.Equal(ReturnStatus.OPEN, (await db.OrderReturns.SingleAsync()).Status);
    }

    [Fact]
    public async Task ARejectedReturnIsNotRejectedAgain() {
        using var db = NewDbContext();
        await Seed(db, ReturnStatus.OPEN);
        var rejection = NewRejection(db);

        await rejection.RejectAsync(ReturnNumber, "first", CancellationToken.None);
        var again = await rejection.RejectAsync(ReturnNumber, "second", CancellationToken.None);

        Assert.Equal(ReturnRejectionStatus.AlreadyRejected, again);
        Assert.Equal("first", (await db.OrderReturns.SingleAsync()).RejectionReason);
    }

    [Fact]
    public async Task AnUnknownReturnIsSaidToBeUnknown() {
        using var db = NewDbContext();

        var outcome = await NewRejection(db).RejectAsync("RMA-NOBODY", "why", CancellationToken.None);

        Assert.Equal(ReturnRejectionStatus.NoSuchReturn, outcome);
    }
}
