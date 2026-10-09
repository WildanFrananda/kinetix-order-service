using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class OrderRowLockPostgresTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly TimeSpan BlockedLongEnough = TimeSpan.FromMilliseconds(750);

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("KINETIX_TEST_POSTGRES");

    private static OrderDbContext NewDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(connectionString)
            .Options
        );

    private static Mock<IEscrowClient> Escrow() {
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.ReleaseHoldAsync(It.IsAny<string>())).ReturnsAsync(StepResult.Ok());
        return escrow;
    }

    private static async Task SeedDeliveredAsync(OrderDbContext db, string orderNumber) {
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM escrow_releases WHERE order_number = {orderNumber}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM order_returns WHERE order_number = {orderNumber}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM orders WHERE order_number = {orderNumber}");

        db.Orders.Add(new OrderEntity {
            OrderNumber = orderNumber,
            CustomerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7",
            MerchantPrincipalId = Merchant,
            Status = OrderStatus.DELIVERED,
            DeliveredAt = DateTime.UtcNow.AddDays(-8),
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AReturnOpenedWhileTheOrderIsCompletingWaitsAndThenSeesTheWindowClosed() {
        if (ConnectionString is null) {
            return;
        }

        const string orderNumber = "ORD-PGLOCK-OPEN";
        using var setup = NewDbContext(ConnectionString);
        await SeedDeliveredAsync(setup, orderNumber);

        using var completing = NewDbContext(ConnectionString);
        await using var transaction = await completing.Database.BeginTransactionAsync();
        await new OrderRowLock(completing).LockAsync(orderNumber, CancellationToken.None);

        using var opening = NewDbContext(ConnectionString);
        var handler = new ReturnsHandler(opening, new OrderRowLock(opening),
            new ReturnRefunds(opening, Escrow().Object, NullLogger<ReturnRefunds>.Instance),
            NullLogger<ReturnsHandler>.Instance
        );
        var open = handler.OpenAsync(orderNumber, Merchant, "Wrong size");

        await Task.WhenAny(open, Task.Delay(BlockedLongEnough));
        Assert.False(open.IsCompleted);

        var order = await completing.Orders.SingleAsync(o => o.OrderNumber == orderNumber);
        order.Status = OrderStatus.COMPLETED;
        await completing.SaveChangesAsync();
        await transaction.CommitAsync();

        var outcome = await open;
        Assert.False(outcome.Success);
        Assert.Contains("window", outcome.Fault, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await setup.OrderReturns.CountAsync(r => r.OrderNumber == orderNumber));
    }

    [Fact]
    public async Task ACompletionThatWaitedOnAReturnBeingOpenedSeesTheReturn() {
        if (ConnectionString is null) {
            return;
        }

        const string orderNumber = "ORD-PGLOCK-COMPLETE";
        using var setup = NewDbContext(ConnectionString);
        await SeedDeliveredAsync(setup, orderNumber);

        using var opening = NewDbContext(ConnectionString);
        await using var transaction = await opening.Database.BeginTransactionAsync();
        await new OrderRowLock(opening).LockAsync(orderNumber, CancellationToken.None);

        using var completing = NewDbContext(ConnectionString);
        var escrow = Escrow();
        var completion = new OrderCompletion(completing, new OrderRowLock(completing), escrow.Object,
            new ReturnWindow(7), NullLogger<OrderCompletion>.Instance
        );
        var complete = completion.CompleteAsync(orderNumber, waitForReturnWindow: true, CancellationToken.None);

        await Task.WhenAny(complete, Task.Delay(BlockedLongEnough));
        Assert.False(complete.IsCompleted);

        opening.OrderReturns.Add(new OrderReturn {
            ReturnNumber = "RMA-PGLOCK-1",
            OrderNumber = orderNumber,
            MerchantPrincipalId = Merchant,
            Reason = "Wrong size",
            Status = ReturnStatus.OPEN,
        });
        await opening.SaveChangesAsync();
        await transaction.CommitAsync();

        var outcome = await complete;
        Assert.Equal(CompletionStatus.ReturnUnresolved, outcome.Status);
        Assert.Equal(
            OrderStatus.DELIVERED,
            (await setup.Orders.AsNoTracking().SingleAsync(o => o.OrderNumber == orderNumber)).Status
        );
        escrow.Verify(e => e.ReleaseHoldAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ALockTakenOutsideATransactionIsRefused() {
        if (ConnectionString is null) {
            return;
        }

        using var db = NewDbContext(ConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OrderRowLock(db).LockAsync("ORD-ANY", CancellationToken.None)
        );
    }
}
