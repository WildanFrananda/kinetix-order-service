using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using DomainStatus = Kinetix.OrderService.Domain.Enums.OrderStatus;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class ReturnsHandlerTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";
    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string OrderNumber = "ORD-RETURN-1";

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options
        );

    private static Mock<IEscrowClient> EscrowRefunding() {
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.RefundGoodsAsync(
            It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()
        )).ReturnsAsync(StepResult.Ok());
        return escrow;
    }

    private static ReturnsHandler NewHandler(OrderDbContext db, IEscrowClient? escrow = null) =>
        new(db, new UnlockedOrderRows(), Refunds(db, escrow ?? EscrowRefunding().Object),
            NullLogger<ReturnsHandler>.Instance
        );

    private static ReturnRefunds Refunds(OrderDbContext db, IEscrowClient escrow) =>
        new(db, escrow, NullLogger<ReturnRefunds>.Instance);

    private static async Task SeedOrder(
        OrderDbContext db,
        DomainStatus status = DomainStatus.DELIVERED,
        decimal voucherDiscount = 20000m,
        params OrderItem[] items
    ) {
        OrderItem[] bought = items.Length > 0 ? items : [
            new OrderItem { ProductId = "GAMIS-RED-M", ProductTitle = "Gamis", UnitPrice = 75000m, Quantity = 2, LineSubtotal = 150000m },
            new OrderItem { ProductId = "GAMIS-BLU-L", ProductTitle = "Gamis", UnitPrice = 50000m, Quantity = 1, LineSubtotal = 50000m },
        ];
        decimal subtotal = bought.Sum(i => i.LineSubtotal);

        db.Orders.Add(new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = Customer,
            MerchantPrincipalId = Merchant,
            Status = status,
            Subtotal = subtotal,
            DiscountAmount = voucherDiscount,
            BaseShippingFee = 9000m,
            FinalShippingFee = 9000m,
            FinalTotal = subtotal - voucherDiscount + 9000m,
            DeliveredAt = status is DomainStatus.DELIVERED ? DateTime.UtcNow.AddDays(-1) : null,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
            Items = [.. bought],
        });

        await db.SaveChangesAsync();
    }

    private static async Task<OrderReturn> Stored(OrderDbContext db, string returnNumber) =>
        await db.OrderReturns.Include(r => r.Lines).FirstAsync(r => r.ReturnNumber == returnNumber);

    [Fact]
    public async Task AReturnIsOpenedAgainstTheOrderItCameFrom() {
        using var db = NewDbContext();
        await SeedOrder(db);

        var outcome = await NewHandler(db).OpenAsync(OrderNumber, Merchant, "Wrong size delivered");

        Assert.True(outcome.Success);
        Assert.False(outcome.AlreadyOpen);
        Assert.StartsWith("RMA-", outcome.ReturnNumber, StringComparison.Ordinal);
        Assert.Equal(ReturnStatus.OPEN, outcome.Status);

        var stored = Assert.Single(db.OrderReturns);
        Assert.Equal(OrderNumber, stored.OrderNumber);
        Assert.Equal("Wrong size delivered", stored.Reason);
    }

    [Fact]
    public async Task AReturnCannotBeOpenedAgainstAnotherMerchantsOrder() {
        using var db = NewDbContext();
        await SeedOrder(db);

        var outcome = await NewHandler(db).OpenAsync(OrderNumber, "77777777-7777-7777-7777-777777777777", "Not mine");

        Assert.False(outcome.Success);
        Assert.Empty(db.OrderReturns);
    }

    [Fact]
    public async Task TheSameReturnAskedForTwiceIsTheSameReturn() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var handler = NewHandler(db);

        var first = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");
        var second = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size again");

        Assert.True(second.Success);
        Assert.True(second.AlreadyOpen);
        Assert.Equal(first.ReturnNumber, second.ReturnNumber);
        Assert.Single(db.OrderReturns);
    }

    [Fact]
    public async Task AReturnAgainstNoOrderIsRefused() {
        using var db = NewDbContext();

        var outcome = await NewHandler(db).OpenAsync("ORD-NOBODY", Merchant, "Wrong size");

        Assert.False(outcome.Success);
        Assert.Contains("no order", outcome.Fault, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.OrderReturns);
    }

    [Fact]
    public async Task AReturnWithNoStatedReasonIsRefused() {
        using var db = NewDbContext();
        await SeedOrder(db);

        var outcome = await NewHandler(db).OpenAsync(OrderNumber, Merchant, "   ");

        Assert.False(outcome.Success);
        Assert.Empty(db.OrderReturns);
    }

    [Fact]
    public async Task GoodsComingBackRefundTheBuyerAndRecordWhereTheyWent() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var handler = NewHandler(db);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        var outcome = await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant,
            [new ReturnedLine("GAMIS-RED-M", 2)],
            "A-01-1",
            new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc)
        );

        Assert.True(outcome.Accepted);
        Assert.False(outcome.AlreadyRecorded);
        Assert.Equal(ReturnStatus.RESOLVED, outcome.Status);

        var stored = await Stored(db, opened.ReturnNumber);

        Assert.Equal("A-01-1", stored.BinCode);
        Assert.Equal(new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc), stored.GoodsReceivedAt);
        Assert.NotNull(stored.ResolvedAt);
        var line = Assert.Single(stored.Lines);
        Assert.Equal("GAMIS-RED-M", line.Sku);
        Assert.Equal(2, line.Quantity);
    }

    [Fact]
    public async Task GoodsReportedTwiceAreNotCountedTwice() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var handler = NewHandler(db);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("GAMIS-RED-M", 2)], "A-01-1", default);

        var again = await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("GAMIS-RED-M", 2)], "A-01-1", default);

        Assert.True(again.Accepted);
        Assert.True(again.AlreadyRecorded);

        var stored = await db.OrderReturns.Include(r => r.Lines)
            .FirstAsync(r => r.ReturnNumber == opened.ReturnNumber);

        Assert.Single(stored.Lines);
    }

    [Fact]
    public async Task GoodsReportedByAnotherMerchantAreRefused() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var handler = NewHandler(db);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        var outcome = await handler.GoodsReceivedAsync(
            opened.ReturnNumber, "99999999-9999-4999-8999-999999999999",
            [new ReturnedLine("GAMIS-RED-M", 2)], "A-01-1", default);

        Assert.False(outcome.Accepted);
        Assert.Contains("another merchant", outcome.Fault, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GoodsWithNothingNamedAreRefusedRatherThanRecordedEmpty() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var handler = NewHandler(db);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        var nothing = await handler.GoodsReceivedAsync(opened.ReturnNumber, Merchant, [], "A-01-1", default);
        var blank = await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("", 3)], "A-01-1", default);
        var zero = await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("GAMIS-RED-M", 0)], "A-01-1", default);

        Assert.False(nothing.Accepted);
        Assert.False(blank.Accepted);
        Assert.False(zero.Accepted);

        var stored = await db.OrderReturns.FirstAsync(r => r.ReturnNumber == opened.ReturnNumber);
        Assert.Equal(ReturnStatus.OPEN, stored.Status);
        Assert.Null(stored.GoodsReceivedAt);
    }

    [Fact]
    public async Task GoodsAgainstAReturnThatDoesNotExistAreRefused() {
        using var db = NewDbContext();

        var outcome = await NewHandler(db).GoodsReceivedAsync(
            "RMA-NOBODY", Merchant, [new ReturnedLine("GAMIS-RED-M", 1)], "A-01-1", default);

        Assert.False(outcome.Accepted);
        Assert.Contains("no return", outcome.Fault, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DomainStatus.PAID)]
    [InlineData(DomainStatus.SHIPPED)]
    [InlineData(DomainStatus.CANCELLED)]
    public async Task AReturnIsOpenedOnlyAgainstADeliveredOrder(DomainStatus status) {
        using var db = NewDbContext();
        await SeedOrder(db, status);

        var outcome = await NewHandler(db).OpenAsync(OrderNumber, Merchant, "Wrong size");

        Assert.False(outcome.Success);
        Assert.Contains("delivered", outcome.Fault, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.OrderReturns);
    }

    [Fact]
    public async Task AReturnCannotBeOpenedOnceTheWindowHasClosed() {
        using var db = NewDbContext();
        await SeedOrder(db, DomainStatus.COMPLETED);

        var outcome = await NewHandler(db).OpenAsync(OrderNumber, Merchant, "Wrong size");

        Assert.False(outcome.Success);
        Assert.Contains("window", outcome.Fault, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.OrderReturns);
    }

    [Fact]
    public async Task OpeningAReturnHoldsTheOrderRow() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var rows = new UnlockedOrderRows();
        var handler = new ReturnsHandler(db, rows, Refunds(db, EscrowRefunding().Object),
            NullLogger<ReturnsHandler>.Instance
        );

        await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        Assert.Equal(1, rows.Locks);
    }

    [Fact]
    public async Task ThePartReturnedIsRefundedInProportionToWhatWasPaidForIt() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = EscrowRefunding();
        var handler = NewHandler(db, escrow.Object);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("GAMIS-RED-M", 2)], "A-01-1", default);

        var stored = await Stored(db, opened.ReturnNumber);
        Assert.Equal(135000m, stored.RefundAmount);
        escrow.Verify(e => e.RefundGoodsAsync(
            OrderNumber, 135000m, "Wrong size", $"return:{opened.ReturnNumber}"
        ), Times.Once);
    }

    [Fact]
    public async Task EverythingReturnedRefundsTheWholeMerchantShare() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = EscrowRefunding();
        var handler = NewHandler(db, escrow.Object);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        await handler.GoodsReceivedAsync(opened.ReturnNumber, Merchant, [
            new ReturnedLine("GAMIS-RED-M", 1),
            new ReturnedLine("GAMIS-BLU-L", 1),
            new ReturnedLine("GAMIS-RED-M", 1),
        ], "A-01-1", default);

        var stored = await Stored(db, opened.ReturnNumber);
        Assert.Equal(180000m, stored.RefundAmount);
        Assert.Equal(ReturnStatus.RESOLVED, stored.Status);
    }

    [Fact]
    public async Task ARefundThatDoesNotDivideEvenlyIsRoundedDownToTheCent() {
        using var db = NewDbContext();
        await SeedOrder(db, DomainStatus.DELIVERED, 1000m,
            new OrderItem { ProductId = "SOCK", ProductTitle = "Sock", UnitPrice = 10000m, Quantity = 3, LineSubtotal = 30000m }
        );
        var handler = NewHandler(db);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Holes");

        await handler.GoodsReceivedAsync(opened.ReturnNumber, Merchant, [new ReturnedLine("SOCK", 1)], "A-01-1", default);

        Assert.Equal(9666.66m, (await Stored(db, opened.ReturnNumber)).RefundAmount);
    }

    [Fact]
    public async Task GoodsThatWereNotOnTheOrderAreRefused() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = EscrowRefunding();
        var handler = NewHandler(db, escrow.Object);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        var outcome = await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("SOMETHING-ELSE", 1)], "A-01-1", default);

        Assert.False(outcome.Accepted);
        Assert.Contains("SOMETHING-ELSE", outcome.Fault, StringComparison.Ordinal);
        var stored = await Stored(db, opened.ReturnNumber);
        Assert.Equal(ReturnStatus.OPEN, stored.Status);
        Assert.Empty(stored.Lines);
        escrow.Verify(e => e.RefundGoodsAsync(
            It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()
        ), Times.Never);
    }

    [Fact]
    public async Task MoreComingBackThanWasBoughtIsRefused() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = EscrowRefunding();
        var handler = NewHandler(db, escrow.Object);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        var outcome = await handler.GoodsReceivedAsync(opened.ReturnNumber, Merchant, [
            new ReturnedLine("GAMIS-RED-M", 2),
            new ReturnedLine("GAMIS-RED-M", 1),
        ], "A-01-1", default);

        Assert.False(outcome.Accepted);
        Assert.Contains("bought 2", outcome.Fault, StringComparison.Ordinal);
        Assert.Equal(ReturnStatus.OPEN, (await Stored(db, opened.ReturnNumber)).Status);
        escrow.Verify(e => e.RefundGoodsAsync(
            It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()
        ), Times.Never);
    }

    [Fact]
    public async Task ARefundPaymentCouldNotTakeIsRetriedWithTheSameKey() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = new Mock<IEscrowClient>();
        escrow.SetupSequence(e => e.RefundGoodsAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.Unavailable, "payment is restarting")))
            .ReturnsAsync(StepResult.Ok());
        var handler = NewHandler(db, escrow.Object);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        var outcome = await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("GAMIS-BLU-L", 1)], "A-01-1", default);

        var stored = await Stored(db, opened.ReturnNumber);
        Assert.True(outcome.Accepted);
        Assert.Equal(ReturnStatus.GOODS_RECEIVED, stored.Status);
        Assert.Equal(1, stored.RefundAttempts);
        Assert.Contains("restarting", stored.LastRefundError, StringComparison.Ordinal);
        Assert.True(stored.NextRefundAttemptAt > DateTime.UtcNow);

        Assert.True(await Refunds(db, escrow.Object).TryRefundAsync(stored, CancellationToken.None));

        Assert.Equal(ReturnStatus.RESOLVED, stored.Status);
        Assert.Null(stored.NextRefundAttemptAt);
        escrow.Verify(e => e.RefundGoodsAsync(
            OrderNumber, 45000m, "Wrong size", $"return:{opened.ReturnNumber}"
        ), Times.Exactly(2));
    }

    [Fact]
    public async Task ARefundPaymentRefusesIsNotRetried() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.RefundGoodsAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.FailedPrecondition, "escrow is RELEASED")));
        var handler = NewHandler(db, escrow.Object);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");

        await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("GAMIS-BLU-L", 1)], "A-01-1", default);

        var stored = await Stored(db, opened.ReturnNumber);
        Assert.Equal(ReturnStatus.GOODS_RECEIVED, stored.Status);
        Assert.Equal("escrow is RELEASED", stored.LastRefundError);
        Assert.Null(stored.NextRefundAttemptAt);
    }

    [Fact]
    public async Task GoodsForAnOrderNoLongerDeliveredAreRecordedButNotRefundedFromEscrow() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var escrow = EscrowRefunding();
        var handler = NewHandler(db, escrow.Object);
        var opened = await handler.OpenAsync(OrderNumber, Merchant, "Wrong size");
        var order = await db.Orders.SingleAsync();
        order.Status = DomainStatus.COMPLETED;
        await db.SaveChangesAsync();

        var outcome = await handler.GoodsReceivedAsync(
            opened.ReturnNumber, Merchant, [new ReturnedLine("GAMIS-BLU-L", 1)], "A-01-1", default);

        var stored = await Stored(db, opened.ReturnNumber);
        Assert.True(outcome.Accepted);
        Assert.Equal(ReturnStatus.GOODS_RECEIVED, stored.Status);
        Assert.Equal(45000m, stored.RefundAmount);
        Assert.Null(stored.NextRefundAttemptAt);
        Assert.Contains("COMPLETED", stored.LastRefundError, StringComparison.Ordinal);
        escrow.Verify(e => e.RefundGoodsAsync(
            It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()
        ), Times.Never);
    }
}
