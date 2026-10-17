using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Delivery;
using Kinetix.OrderService.Application.Fulfillment;
using Kinetix.OrderService.Application.Lifecycle;
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

public class OrderLifecycleRecordingTests {
    private const string OrderNumber = "ORD-LIFECYCLE-1";
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";
    private const string Driver = "11111111-2222-3333-4444-555555555555";
    private const string Admin = "0b6f1c7e-2d4a-4e8b-9c3f-5a1d7e9b2c40";

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options
        );

    private static async Task<OrderEntity> SeedOrder(OrderDbContext db, OrderStatus status) {
        var order = new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = Buyer,
            MerchantPrincipalId = Merchant,
            Status = status,
            Subtotal = 100000m,
            FinalTotal = 110000m,
            FinalShippingFee = 10000m,
            DeliveredAt = DateTime.UtcNow.AddDays(-8),
            Items = [
                new OrderItem {
                    ProductId = "SKU-1",
                    ProductTitle = "Sepatu",
                    UnitPrice = 100000m,
                    Quantity = 1,
                    LineSubtotal = 100000m,
                },
            ],
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return order;
    }

    private static async Task<OrderReturn> SeedReturn(OrderDbContext db, ReturnStatus status) {
        var record = new OrderReturn {
            ReturnNumber = "RMA-LIFECYCLE-1",
            OrderNumber = OrderNumber,
            MerchantPrincipalId = Merchant,
            Reason = "Wrong size",
            Status = status,
        };

        db.OrderReturns.Add(record);
        await db.SaveChangesAsync();

        return record;
    }

    private static Mock<IEscrowClient> EscrowAgreeing() {
        var escrow = new Mock<IEscrowClient>();
        escrow
            .Setup(e => e.SettleShippingFeeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(StepResult.Ok());
        escrow.Setup(e => e.ReleaseHoldAsync(It.IsAny<string>())).ReturnsAsync(StepResult.Ok());
        escrow
            .Setup(e => e.RefundGoodsAsync(
                It.IsAny<string>(),
                It.IsAny<decimal>(),
                It.IsAny<string>(),
                It.IsAny<string>()
            ))
            .ReturnsAsync(StepResult.Ok());
        return escrow;
    }

    private static async Task<OrderLifecycleEvent> TheOnlyEvent(OrderDbContext db) =>
        await db.OrderLifecycleEvents.AsNoTracking().SingleAsync();

    [Fact]
    public void AStatusChangeRecordsWhereItCameFromWhereItWentWhoMovedItAndWhen() {
        using var db = NewDbContext();
        var order = new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = Buyer,
            MerchantPrincipalId = Merchant,
            Status = OrderStatus.PAID,
        };
        var at = new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Utc);

        db.ChangeStatus(order, OrderStatus.PROCESSING_FULFILLMENT, LifecycleActor.Merchant(Merchant), at);

        var recorded = Assert.Single(db.ChangeTracker.Entries<OrderLifecycleEvent>()).Entity;
        Assert.Equal(OrderStatus.PROCESSING_FULFILLMENT, order.Status);
        Assert.Equal(at, order.UpdatedAt);
        Assert.Equal(LifecycleEventKind.STATUS_CHANGED, recorded.Kind);
        Assert.Equal(OrderStatus.PAID, recorded.FromStatus);
        Assert.Equal(OrderStatus.PROCESSING_FULFILLMENT, recorded.ToStatus);
        Assert.Equal(LifecycleActorKind.MERCHANT, recorded.ActorKind);
        Assert.Equal(Merchant, recorded.ActorPrincipalId);
        Assert.Equal(Buyer, recorded.BuyerPrincipalId);
        Assert.Equal(Merchant, recorded.MerchantPrincipalId);
        Assert.Equal(at, recorded.OccurredAt);
    }

    [Fact]
    public void MovingAnOrderToTheStatusItAlreadyHasIsNotATransition() {
        using var db = NewDbContext();
        var order = new OrderEntity { OrderNumber = OrderNumber, Status = OrderStatus.CANCELLED };

        db.ChangeStatus(order, OrderStatus.CANCELLED, LifecycleActor.System, DateTime.UtcNow);

        Assert.Empty(db.ChangeTracker.Entries<OrderLifecycleEvent>());
    }

    [Fact]
    public async Task APackedOrderIsLoggedAsTheMerchantsTransition() {
        using var db = NewDbContext();
        await SeedOrder(db, OrderStatus.PAID);

        await new FulfillmentPackedHandler(db, new Mock<ICourierDispatcher>().Object)
            .HandleAsync(Merchant, OrderNumber, "TASK-1");

        var recorded = await TheOnlyEvent(db);
        Assert.Equal(OrderStatus.PAID, recorded.FromStatus);
        Assert.Equal(OrderStatus.PROCESSING_FULFILLMENT, recorded.ToStatus);
        Assert.Equal(LifecycleActorKind.MERCHANT, recorded.ActorKind);
        Assert.Equal(Merchant, recorded.ActorPrincipalId);
    }

    [Fact]
    public async Task ARepeatedPackedReportLogsNothingMore() {
        using var db = NewDbContext();
        await SeedOrder(db, OrderStatus.PAID);
        var handler = new FulfillmentPackedHandler(db, new Mock<ICourierDispatcher>().Object);

        await handler.HandleAsync(Merchant, OrderNumber, "TASK-1");
        await handler.HandleAsync(Merchant, OrderNumber, "TASK-1");

        Assert.Equal(1, await db.OrderLifecycleEvents.CountAsync());
    }

    [Fact]
    public async Task ADeliveryIsLoggedAsTheDriversTransition() {
        using var db = NewDbContext();
        await SeedOrder(db, OrderStatus.PROCESSING_FULFILLMENT);

        await new OrderDeliveredHandler(db, EscrowAgreeing().Object, NullLogger<OrderDeliveredHandler>.Instance)
            .HandleAsync(OrderNumber, Driver, DateTime.UtcNow);

        var recorded = await TheOnlyEvent(db);
        Assert.Equal(OrderStatus.PROCESSING_FULFILLMENT, recorded.FromStatus);
        Assert.Equal(OrderStatus.DELIVERED, recorded.ToStatus);
        Assert.Equal(LifecycleActorKind.DRIVER, recorded.ActorKind);
        Assert.Equal(Driver, recorded.ActorPrincipalId);
    }

    [Fact]
    public async Task ADeliveryReportedAgainstARefundedOrderLogsNoTransition() {
        using var db = NewDbContext();
        await SeedOrder(db, OrderStatus.REFUNDED);

        await new OrderDeliveredHandler(db, EscrowAgreeing().Object, NullLogger<OrderDeliveredHandler>.Instance)
            .HandleAsync(OrderNumber, Driver, DateTime.UtcNow);

        Assert.Empty(db.OrderLifecycleEvents);
    }

    [Theory]
    [InlineData(LifecycleActorKind.SYSTEM, null)]
    [InlineData(LifecycleActorKind.ADMIN, Admin)]
    public async Task ACompletionIsLoggedAsWhoeverCompletedIt(LifecycleActorKind kind, string? principal) {
        using var db = NewDbContext();
        await SeedOrder(db, OrderStatus.DELIVERED);
        var completion = new OrderCompletion(
            db,
            new UnlockedOrderRows(),
            EscrowAgreeing().Object,
            new ReturnWindow(7),
            NullLogger<OrderCompletion>.Instance
        );

        await completion.CompleteAsync(OrderNumber, true, new LifecycleActor(kind, principal), CancellationToken.None);

        var recorded = await TheOnlyEvent(db);
        Assert.Equal(OrderStatus.DELIVERED, recorded.FromStatus);
        Assert.Equal(OrderStatus.COMPLETED, recorded.ToStatus);
        Assert.Equal(kind, recorded.ActorKind);
        Assert.Equal(principal, recorded.ActorPrincipalId);
    }

    [Fact]
    public async Task EveryStepOfAReturnIsLoggedWithTheBuyerItIsOwedTo() {
        using var db = NewDbContext();
        await SeedOrder(db, OrderStatus.DELIVERED);
        var handler = new ReturnsHandler(
            db,
            new UnlockedOrderRows(),
            new ReturnRefunds(db, EscrowAgreeing().Object, NullLogger<ReturnRefunds>.Instance),
            NullLogger<ReturnsHandler>.Instance
        );

        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");
        await handler.GoodsReceivedAsync(
            opened.ReturnNumber,
            Merchant,
            [new ReturnedLine("SKU-1", 1)],
            "BIN-1",
            DateTime.UtcNow
        );

        var steps = await db.OrderLifecycleEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync();
        Assert.Equal(
            [
                (LifecycleEventKind.RETURN_OPENED, ReturnStatus.OPEN, LifecycleActorKind.MERCHANT),
                (LifecycleEventKind.RETURN_GOODS_RECEIVED, ReturnStatus.GOODS_RECEIVED, LifecycleActorKind.MERCHANT),
                (LifecycleEventKind.RETURN_RESOLVED, ReturnStatus.RESOLVED, LifecycleActorKind.SYSTEM),
            ],
            steps.Select(e => (e.Kind, e.ReturnStatus!.Value, e.ActorKind))
        );
        Assert.All(steps, e => Assert.Equal(opened.ReturnNumber, e.ReturnNumber));
        Assert.All(steps, e => Assert.Equal(Buyer, e.BuyerPrincipalId));
        Assert.All(steps, e => Assert.Equal(Merchant, e.MerchantPrincipalId));
        Assert.All(steps, e => Assert.Null(e.ToStatus));
    }

    [Fact]
    public async Task ARejectedReturnIsLoggedAsTheAdminsDecision() {
        using var db = NewDbContext();
        await SeedOrder(db, OrderStatus.DELIVERED);
        await SeedReturn(db, ReturnStatus.OPEN);

        await new ReturnRejection(db, new UnlockedOrderRows(), NullLogger<ReturnRejection>.Instance)
            .RejectAsync("RMA-LIFECYCLE-1", "nothing came back", LifecycleActor.Admin(Admin), CancellationToken.None);

        var recorded = await TheOnlyEvent(db);
        Assert.Equal(LifecycleEventKind.RETURN_REJECTED, recorded.Kind);
        Assert.Equal(ReturnStatus.REJECTED, recorded.ReturnStatus);
        Assert.Equal(LifecycleActorKind.ADMIN, recorded.ActorKind);
        Assert.Equal(Admin, recorded.ActorPrincipalId);
        Assert.Equal(Buyer, recorded.BuyerPrincipalId);
    }

    [Fact]
    public async Task ARefundPaymentRefusesLeavesTheReturnUnresolvedAndLogsNothing() {
        using var db = NewDbContext();
        await SeedOrder(db, OrderStatus.DELIVERED);
        var record = await SeedReturn(db, ReturnStatus.GOODS_RECEIVED);
        record.RefundAmount = 50000m;
        await db.SaveChangesAsync();

        var escrow = new Mock<IEscrowClient>();
        escrow
            .Setup(e => e.RefundGoodsAsync(
                It.IsAny<string>(),
                It.IsAny<decimal>(),
                It.IsAny<string>(),
                It.IsAny<string>()
            ))
            .ReturnsAsync(StepResult.Refused("the hold is already released"));

        await new ReturnRefunds(db, escrow.Object, NullLogger<ReturnRefunds>.Instance)
            .TryRefundAsync(record, CancellationToken.None);

        Assert.Equal(ReturnStatus.GOODS_RECEIVED, record.Status);
        Assert.Empty(db.OrderLifecycleEvents);
    }
}
