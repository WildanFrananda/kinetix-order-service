using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
            .Options
        );

    private static ReturnsHandler NewHandler(OrderDbContext db) =>
        new(db, NullLogger<ReturnsHandler>.Instance);

    private static async Task SeedOrder(OrderDbContext db) {
        db.Orders.Add(new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = Customer,
            Status = DomainStatus.DELIVERED,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
        });

        await db.SaveChangesAsync();
    }

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
    public async Task GoodsComingBackAdvancesTheReturnAndRecordsWhereTheyWent() {
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
        Assert.Equal(ReturnStatus.GOODS_RECEIVED, outcome.Status);

        var stored = await db.OrderReturns.Include(r => r.Lines)
            .FirstAsync(r => r.ReturnNumber == opened.ReturnNumber);

        Assert.Equal("A-01-1", stored.BinCode);
        Assert.Equal(new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc), stored.GoodsReceivedAt);
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
}
