using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Fulfillment;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Fleet.V1;
using DomainStatus = Kinetix.OrderService.Domain.Enums.OrderStatus;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class FulfillmentPackedGuardTests {
    private const string Merchant = "11111111-1111-1111-1111-111111111111";
    private const string Rival = "33333333-3333-3333-3333-333333333333";
    private const string OrderNumber = "ORD-PACKED-1";

    private sealed class Everywhere : IAddressDirectory {
        public Task<MapPoint?> PickupPointAsync(string merchantPrincipalId) =>
            Task.FromResult<MapPoint?>(new MapPoint(-6.1754, 106.8272));

        public Task<MapPoint?> DeliveryPointAsync(string customerPrincipalId) =>
            Task.FromResult<MapPoint?>(new MapPoint(-6.2088, 106.8456));
    }

    private sealed class CountingCourier : CourierTelemetryService.CourierTelemetryServiceClient {
        public int Calls { get; private set; }

        public override AsyncUnaryCall<DispatchCourierResponse> DispatchCourierAsync(
            DispatchCourierRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default
        ) {
            Calls += 1;
            return new AsyncUnaryCall<DispatchCourierResponse>(
                Task.FromResult(new DispatchCourierResponse { Success = true, DispatchRef = "FLEET-1" }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );
        }
    }

    private sealed class SilentWarehouse : IFulfillmentClient {
        public Task<FulfillmentCreated> CreateTaskAsync(
            string merchantPrincipalId, string orderNumber, IReadOnlyList<FulfillmentLine> lines
        ) => throw new NotSupportedException("not used by these tests");

        public Task<StepResult> CancelTaskAsync(string merchantPrincipalId, string fulfillmentTaskId) =>
            throw new NotSupportedException("not used by these tests");

        public Task<StepResult> RecordCourierAwbAsync(
            string merchantPrincipalId, string fulfillmentTaskId, string orderNumber, string awbNumber
        ) => Task.FromResult(StepResult.Ok());
    }

    private static async Task<OrderDbContext> WithOrder(DomainStatus status) {
        var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options
        );
        db.Orders.Add(new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = "22222222-2222-2222-2222-222222222222",
            MerchantPrincipalId = Merchant,
            Status = status,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static FulfillmentPackedHandler Handler(OrderDbContext db, CountingCourier courier) =>
        new(db, courier, new Everywhere(), new SilentWarehouse(), NullLogger<FulfillmentPackedHandler>.Instance);

    [Theory]
    [InlineData(DomainStatus.CANCELLED)]
    [InlineData(DomainStatus.REFUNDED)]
    [InlineData(DomainStatus.PENDING_PAYMENT)]
    public async Task AnOrderThatIsNotPaidForIsNotRevivedNorSentACourier(DomainStatus status) {
        using var db = await WithOrder(status);
        var courier = new CountingCourier();

        var outcome = await Handler(db, courier).HandleAsync(Merchant, OrderNumber, "TASK-1");

        Assert.False(outcome.Found);
        Assert.Equal("ORDER_NOT_AWAITING_FULFILMENT", outcome.RefusalCode);
        Assert.Contains(status.ToString(), outcome.Detail);
        Assert.Equal(status, (await db.Orders.SingleAsync()).Status);
        Assert.Equal(0, courier.Calls);
    }

    [Fact]
    public async Task AnotherMerchantCannotReportThisOrderPacked() {
        using var db = await WithOrder(DomainStatus.PAID);
        var courier = new CountingCourier();

        var outcome = await Handler(db, courier).HandleAsync(Rival, OrderNumber, "TASK-1");

        Assert.False(outcome.Found);
        Assert.Equal("NOT_THIS_MERCHANTS_ORDER", outcome.RefusalCode);
        Assert.Equal(DomainStatus.PAID, (await db.Orders.SingleAsync()).Status);
        Assert.Equal(0, courier.Calls);
    }

    [Theory]
    [InlineData(DomainStatus.PAID)]
    [InlineData(DomainStatus.PROCESSING_FULFILLMENT)]
    public async Task APaidOrderIsSentACourierAndAReportRepeatedIsAccepted(DomainStatus status) {
        using var db = await WithOrder(status);
        var courier = new CountingCourier();

        var outcome = await Handler(db, courier).HandleAsync(Merchant, OrderNumber, "TASK-1");

        Assert.True(outcome.Found);
        Assert.Equal(DomainStatus.PROCESSING_FULFILLMENT, (await db.Orders.SingleAsync()).Status);
        Assert.Equal(1, courier.Calls);
    }
}
