using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Background;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class OrderCompletionSweeperTests {
    private static readonly ReturnWindow SevenDays = new(7);

    private static (ServiceProvider Services, Mock<IEscrowClient> Escrow) Services() {
        var database = Guid.NewGuid().ToString();
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.ReleaseHoldAsync(It.IsAny<string>())).ReturnsAsync(StepResult.Ok());
        escrow.Setup(e => e.RefundGoodsAsync(
            It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()
        )).ReturnsAsync(StepResult.Ok());

        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<OrderDbContext>(options => options
                .UseInMemoryDatabase(database)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            )
            .AddSingleton(SevenDays)
            .AddSingleton(escrow.Object)
            .AddScoped<IOrderRowLock, UnlockedOrderRows>()
            .AddScoped<IReturnRefunds, ReturnRefunds>()
            .AddScoped<IOrderCompletion, OrderCompletion>()
            .BuildServiceProvider();

        return (services, escrow);
    }

    private static OrderCompletionSweeper SweeperOver(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), SevenDays,
            new ConfigurationBuilder().Build(), NullLogger<OrderCompletionSweeper>.Instance
        );

    private static OrderEntity Delivered(string orderNumber, double daysAgo) => new() {
        OrderNumber = orderNumber,
        CustomerPrincipalId = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7",
        MerchantPrincipalId = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
        Status = OrderStatus.DELIVERED,
        DeliveredAt = DateTime.UtcNow.AddDays(-daysAgo),
    };

    private static OrderReturn Return(string returnNumber, string orderNumber, ReturnStatus status) => new() {
        ReturnNumber = returnNumber,
        OrderNumber = orderNumber,
        MerchantPrincipalId = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
        Reason = "Wrong size",
        Status = status,
    };

    [Fact]
    public async Task ASweepCompletesOnlyOrdersWhoseWindowHasClosedWithNothingOutstanding() {
        var (services, escrow) = Services();
        using (var scope = services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            db.Orders.AddRange(
                Delivered("ORD-DUE", 8),
                Delivered("ORD-NOT-YET", 6),
                Delivered("ORD-RETURNING", 8)
            );
            db.OrderReturns.Add(Return("RMA-OPEN", "ORD-RETURNING", ReturnStatus.OPEN));
            await db.SaveChangesAsync();
        }

        await SweeperOver(services).SweepAsync(CancellationToken.None);

        using var check = services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = await after.Orders.ToDictionaryAsync(o => o.OrderNumber, o => o.Status);
        Assert.Equal(OrderStatus.COMPLETED, statuses["ORD-DUE"]);
        Assert.Equal(OrderStatus.DELIVERED, statuses["ORD-NOT-YET"]);
        Assert.Equal(OrderStatus.DELIVERED, statuses["ORD-RETURNING"]);
        escrow.Verify(e => e.ReleaseHoldAsync("ORD-DUE"), Times.Once);
        escrow.Verify(e => e.ReleaseHoldAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ASweepRefundsOwedReturnsBeforeItLooksForOrdersToComplete() {
        var (services, escrow) = Services();
        using (var scope = services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            db.Orders.Add(Delivered("ORD-REFUNDING", 8));
            var owed = Return("RMA-OWED", "ORD-REFUNDING", ReturnStatus.GOODS_RECEIVED);
            owed.RefundAmount = 45000m;
            owed.NextRefundAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            db.OrderReturns.Add(owed);
            await db.SaveChangesAsync();
        }

        await SweeperOver(services).SweepAsync(CancellationToken.None);

        using var check = services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Equal(ReturnStatus.RESOLVED, (await after.OrderReturns.SingleAsync()).Status);
        Assert.Equal(OrderStatus.COMPLETED, (await after.Orders.SingleAsync()).Status);
        escrow.Verify(e => e.RefundGoodsAsync("ORD-REFUNDING", 45000m, "Wrong size", "return:RMA-OWED"), Times.Once);
    }

    [Fact]
    public async Task ASweepRetriesAReleaseThatIsDueAndLeavesAHaltedOneAlone() {
        var (services, escrow) = Services();
        using (var scope = services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            db.EscrowReleases.AddRange(
                new EscrowRelease { OrderNumber = "ORD-DUE", Attempts = 1, NextAttemptAt = DateTime.UtcNow.AddMinutes(-1) },
                new EscrowRelease { OrderNumber = "ORD-LATER", Attempts = 1, NextAttemptAt = DateTime.UtcNow.AddMinutes(5) },
                new EscrowRelease { OrderNumber = "ORD-HALTED", Attempts = 1, LastError = "escrow is REFUNDED" }
            );
            await db.SaveChangesAsync();
        }

        await SweeperOver(services).SweepAsync(CancellationToken.None);

        escrow.Verify(e => e.ReleaseHoldAsync("ORD-DUE"), Times.Once);
        escrow.Verify(e => e.ReleaseHoldAsync(It.IsAny<string>()), Times.Once);
    }
}
