using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Background;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Kinetix.OrderService.Tests;

public class ShippingSettlementSweeperTests {
    private const string OrderNumber = "ORD-SETTLE-RETRY";
    private const string Driver = "11111111-2222-3333-4444-555555555555";

    public static TheoryData<Exception, string> FailedSettlements() => new() {
        {
            new RpcException(new Status(
                StatusCode.Unavailable,
                "Error starting gRPC call. HttpRequestException: Connection refused (kinetix-payment-service:50056)"
            )),
            "PAYMENT_UNAVAILABLE"
        },
        { new RpcException(new Status(StatusCode.FailedPrecondition, "escrow is REFUNDED")), "PAYMENT_REFUSED" },
        { new InvalidOperationException("could not read /pki/tls.crt"), "UNEXPECTED_FAILURE" },
    };

    [Theory]
    [MemberData(nameof(FailedSettlements))]
    public async Task ARetryThatFailedStoresAFixedReasonAndNeverTheExceptionText(Exception failure, string reason) {
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.SettleShippingFeeAsync(OrderNumber, Driver)).ThrowsAsync(failure);

        var settlement = await SweptAsync(escrow.Object);

        Assert.Null(settlement.SettledAt);
        Assert.Equal(2, settlement.Attempts);
        Assert.True(settlement.NextAttemptAt > DateTime.UtcNow);
        Assert.Equal(reason, settlement.LastError);
    }

    [Fact]
    public async Task ARetryPaymentAcceptsSettlesTheFee() {
        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.SettleShippingFeeAsync(OrderNumber, Driver)).ReturnsAsync(StepResult.Ok());

        var settlement = await SweptAsync(escrow.Object);

        Assert.NotNull(settlement.SettledAt);
        Assert.Null(settlement.NextAttemptAt);
        Assert.Null(settlement.LastError);
    }

    private static async Task<ShippingSettlement> SweptAsync(IEscrowClient escrow) {
        var database = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddDbContext<OrderDbContext>(options => options.UseInMemoryDatabase(database))
            .AddSingleton(escrow)
            .BuildServiceProvider();

        using (var scope = services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            db.ShippingSettlements.Add(new ShippingSettlement {
                OrderNumber = OrderNumber,
                DriverPrincipalId = Driver,
                DeliveredAt = DateTime.UtcNow.AddMinutes(-5),
                Attempts = 1,
                LastError = "PAYMENT_UNAVAILABLE",
                NextAttemptAt = DateTime.UtcNow.AddSeconds(-1),
            });
            await db.SaveChangesAsync();
        }

        await new ShippingSettlementSweeper(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["KINETIX_SETTLEMENT_SWEEP_BATCH"] = "20" })
                .Build(),
            NullLogger<ShippingSettlementSweeper>.Instance
        ).SweepAsync(CancellationToken.None);

        using (var scope = services.CreateScope()) {
            return await scope.ServiceProvider.GetRequiredService<OrderDbContext>()
                .ShippingSettlements
                .SingleAsync();
        }
    }
}
