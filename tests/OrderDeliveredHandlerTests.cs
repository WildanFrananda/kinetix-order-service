using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Delivery;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using DomainStatus = Kinetix.OrderService.Domain.Enums.OrderStatus;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class OrderDeliveredHandlerTests {
    private const string Driver = "11111111-2222-3333-4444-555555555555";

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options
        );

    private sealed class StubEscrow : IEscrowClient {
        public StepResult Settlement { get; init; } = StepResult.Ok();
        public Exception? Throws { get; init; }
        public int Calls { get; private set; }
        public string? LastOrderNumber { get; private set; }
        public string? LastDriverPrincipalId { get; private set; }

        public Task<StepResult> SettleShippingFeeAsync(string orderNumber, string driverPrincipalId) {
            Calls += 1;
            LastOrderNumber = orderNumber;
            LastDriverPrincipalId = driverPrincipalId;

            if (Throws is not null) {
                throw Throws;
            }

            return Task.FromResult(Settlement);
        }

        public Task<StepResult> CreateHoldAsync(
            string orderNumber, string customerPrincipalId, string merchantPrincipalId,
            string? driverPrincipalId, decimal totalOrderAmount, decimal merchantAmount,
            decimal shippingFeeAmount
        ) => throw new NotSupportedException();

        public Task<StepResult> RefundHoldAsync(string orderNumber, string reason) =>
            throw new NotSupportedException();

        public Task<EscrowStanding> GetStandingAsync(string orderNumber) =>
            throw new NotSupportedException();
    }

    private static OrderDeliveredHandler NewHandler(OrderDbContext db, IEscrowClient escrow) =>
        new(db, escrow, NullLogger<OrderDeliveredHandler>.Instance);

    private static async Task<OrderEntity> SeedOrder(
        OrderDbContext db, string orderNumber, DomainStatus status = DomainStatus.SHIPPED
    ) {
        var order = new OrderEntity {
            OrderNumber = orderNumber,
            CustomerPrincipalId = "99999999-9999-9999-9999-999999999999",
            Status = status,
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task ADeliveryMarksTheOrderDeliveredAndRecordsTheFeeAsOwed() {
        using var db = NewDbContext();
        await SeedOrder(db, "ORD-S2-0001");
        var escrow = new StubEscrow();

        var outcome = await NewHandler(db, escrow).HandleAsync("ORD-S2-0001", Driver, DateTime.UtcNow);

        Assert.True(outcome.Accepted);
        Assert.False(outcome.AlreadyDelivered);

        var order = await db.Orders.SingleAsync(o => o.OrderNumber == "ORD-S2-0001");
        Assert.Equal(DomainStatus.DELIVERED, order.Status);

        var settlement = await db.ShippingSettlements.SingleAsync();
        Assert.Equal(Driver, settlement.DriverPrincipalId);
        Assert.NotNull(settlement.SettledAt);
    }

    [Fact]
    public async Task ThePaymentIsAttemptedForTheCourierWhoActuallyDelivered() {
        using var db = NewDbContext();
        await SeedOrder(db, "ORD-S2-0002");
        var escrow = new StubEscrow();

        await NewHandler(db, escrow).HandleAsync("ORD-S2-0002", Driver, DateTime.UtcNow);

        Assert.Equal(1, escrow.Calls);
        Assert.Equal("ORD-S2-0002", escrow.LastOrderNumber);
        Assert.Equal(Driver, escrow.LastDriverPrincipalId);
    }

    [Fact]
    public async Task WhenPaymentIsUnreachableTheFeeIsStillRecordedAsOwedAndDueForRetry() {
        using var db = NewDbContext();
        await SeedOrder(db, "ORD-S2-0003");
        var escrow = new StubEscrow { Throws = new InvalidOperationException("payment is down") };

        var outcome = await NewHandler(db, escrow).HandleAsync("ORD-S2-0003", Driver, DateTime.UtcNow);

        Assert.True(outcome.Accepted);

        var settlement = await db.ShippingSettlements.SingleAsync();
        Assert.Null(settlement.SettledAt);
        Assert.NotNull(settlement.NextAttemptAt);
        Assert.Equal(1, settlement.Attempts);
        Assert.Contains("payment is down", settlement.LastError);
    }

    [Fact]
    public async Task WhenPaymentRefusesTheFeeIsOwedRatherThanForgotten() {
        using var db = NewDbContext();
        await SeedOrder(db, "ORD-S2-0004");
        var escrow = new StubEscrow { Settlement = StepResult.Absent(false, "no hold for this order") };

        await NewHandler(db, escrow).HandleAsync("ORD-S2-0004", Driver, DateTime.UtcNow);

        var settlement = await db.ShippingSettlements.SingleAsync();
        Assert.Null(settlement.SettledAt);
        Assert.NotNull(settlement.NextAttemptAt);
        Assert.Equal("no hold for this order", settlement.LastError);
    }

    [Fact]
    public async Task ARepeatedReportIsIdempotentAndDoesNotPayTwice() {
        using var db = NewDbContext();
        await SeedOrder(db, "ORD-S2-0005");
        var escrow = new StubEscrow();
        var handler = NewHandler(db, escrow);

        await handler.HandleAsync("ORD-S2-0005", Driver, DateTime.UtcNow);
        var second = await handler.HandleAsync("ORD-S2-0005", Driver, DateTime.UtcNow);

        Assert.True(second.Accepted);
        Assert.True(second.AlreadyDelivered);
        Assert.Equal(1, escrow.Calls);
        Assert.Single(db.ShippingSettlements);
    }

    [Fact]
    public async Task AnOrderNobodyHasHeardOfIsRefusedRatherThanRecorded() {
        using var db = NewDbContext();
        var escrow = new StubEscrow();

        var outcome = await NewHandler(db, escrow).HandleAsync("ORD-NOPE", Driver, DateTime.UtcNow);

        Assert.False(outcome.Accepted);
        Assert.Equal(0, escrow.Calls);
        Assert.Empty(db.ShippingSettlements);
    }

    [Fact]
    public async Task ADeliveryWithNoCourierIsRefused() {
        using var db = NewDbContext();
        await SeedOrder(db, "ORD-S2-0006");
        var escrow = new StubEscrow();

        var outcome = await NewHandler(db, escrow).HandleAsync("ORD-S2-0006", "  ", DateTime.UtcNow);

        Assert.False(outcome.Accepted);
        Assert.Equal(0, escrow.Calls);
        Assert.Empty(db.ShippingSettlements);
    }

    [Fact]
    public async Task ARefundedOrderKeepsItsStatusButTheFeeIsStillRecorded() {
        using var db = NewDbContext();
        await SeedOrder(db, "ORD-S2-0007", DomainStatus.REFUNDED);
        var escrow = new StubEscrow();

        var outcome = await NewHandler(db, escrow).HandleAsync("ORD-S2-0007", Driver, DateTime.UtcNow);

        Assert.True(outcome.Accepted);

        var order = await db.Orders.SingleAsync(o => o.OrderNumber == "ORD-S2-0007");
        Assert.Equal(DomainStatus.REFUNDED, order.Status);
        Assert.Single(db.ShippingSettlements);
    }

    [Fact]
    public async Task TheDeliveryTimeMatchingReportedIsWhatIsRecorded() {
        using var db = NewDbContext();
        await SeedOrder(db, "ORD-S2-0008");
        var delivered = new DateTime(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

        await NewHandler(db, new StubEscrow()).HandleAsync("ORD-S2-0008", Driver, delivered);

        var settlement = await db.ShippingSettlements.SingleAsync();
        Assert.Equal(delivered, settlement.DeliveredAt);
    }
}
