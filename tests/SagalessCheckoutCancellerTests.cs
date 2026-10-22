using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class SagalessCheckoutCancellerTests {
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options
        );

    private static async Task PlaceAsync(OrderDbContext db, OrderStatus status, TimeSpan age) {
        db.Orders.Add(new OrderEntity {
            OrderNumber = "ORD-SAGALESS-0001",
            CustomerPrincipalId = Buyer,
            MerchantPrincipalId = Merchant,
            Status = status,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta",
            RecipientName = "Test Buyer",
            RecipientPhone = "081200000000",
            CreatedAt = DateTime.UtcNow - age,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static SagalessCheckoutCanceller Canceller(OrderDbContext db) =>
        new(db, new UnlockedOrderRows(), NullLogger<SagalessCheckoutCanceller>.Instance);

    [Fact]
    public async Task AnOrderWhoseCheckoutNeverStartedItsSagaIsCancelledOnceTheGraceHasPassed() {
        using var db = NewDbContext();
        await PlaceAsync(db, OrderStatus.PENDING_PAYMENT, TimeSpan.FromMinutes(16));

        Assert.Equal(1, await Canceller(db).CancelAsync(Grace, 20, CancellationToken.None));

        Assert.Equal(OrderStatus.CANCELLED, (await db.Orders.SingleAsync()).Status);

        var logged = await db.OrderLifecycleEvents.SingleAsync();
        Assert.Equal(OrderStatus.PENDING_PAYMENT, logged.FromStatus);
        Assert.Equal(OrderStatus.CANCELLED, logged.ToStatus);
        Assert.Equal(LifecycleActorKind.SYSTEM, logged.ActorKind);

        var saga = await db.CheckoutSagas.SingleAsync();
        Assert.Equal(SagaState.Compensated, saga.State);
        Assert.Equal(SagalessCheckoutCanceller.Reason, saga.FailureReason);
        Assert.Empty(db.CheckoutSagaSteps);
    }

    [Fact]
    public async Task AnOrderStillInsideTheGraceIsLeftToTheCheckoutThatPlacedIt() {
        using var db = NewDbContext();
        await PlaceAsync(db, OrderStatus.PENDING_PAYMENT, TimeSpan.FromMinutes(14));

        Assert.Equal(0, await Canceller(db).CancelAsync(Grace, 20, CancellationToken.None));
        Assert.Equal(OrderStatus.PENDING_PAYMENT, (await db.Orders.SingleAsync()).Status);
        Assert.Empty(db.CheckoutSagas);
    }

    [Theory]
    [InlineData(SagaState.Running)]
    [InlineData(SagaState.Completed)]
    [InlineData(SagaState.Compensating)]
    [InlineData(SagaState.Compensated)]
    [InlineData(SagaState.Stuck)]
    [InlineData(SagaState.Abandoned)]
    public async Task AnOrderWithASagaInAnyStateIsNeverCancelledHere(SagaState state) {
        using var db = NewDbContext();
        await PlaceAsync(db, OrderStatus.PENDING_PAYMENT, TimeSpan.FromHours(1));
        db.CheckoutSagas.Add(new CheckoutSaga {
            OrderNumber = "ORD-SAGALESS-0001",
            CustomerPrincipalId = Buyer,
            State = state,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0, await Canceller(db).CancelAsync(Grace, 20, CancellationToken.None));
        Assert.Equal(OrderStatus.PENDING_PAYMENT, (await db.Orders.SingleAsync()).Status);
        Assert.Empty(db.OrderLifecycleEvents);
    }

    [Fact]
    public async Task AnOrderNoLongerAwaitingPaymentIsLeftAlone() {
        using var db = NewDbContext();
        await PlaceAsync(db, OrderStatus.PAID, TimeSpan.FromHours(1));

        Assert.Equal(0, await Canceller(db).CancelAsync(Grace, 20, CancellationToken.None));
        Assert.Empty(db.CheckoutSagas);
    }
}
