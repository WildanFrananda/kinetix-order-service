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

public class DispatchNeedsBothPointsTests {
    private const string Merchant = "11111111-1111-1111-1111-111111111111";
    private const string Customer = "22222222-2222-2222-2222-222222222222";

    private sealed class StubDirectory(MapPoint? pickup, MapPoint? delivery) : IAddressDirectory {
        public Task<MapPoint?> PickupPointAsync(string merchantPrincipalId) =>
            Task.FromResult(pickup);

        public Task<MapPoint?> DeliveryPointAsync(string customerPrincipalId) =>
            Task.FromResult(delivery);
    }

    private sealed class RecordingCourier : CourierTelemetryService.CourierTelemetryServiceClient {
        public DispatchCourierRequest? LastRequest { get; private set; }
        public int Calls { get; private set; }

        public override AsyncUnaryCall<DispatchCourierResponse> DispatchCourierAsync(
            DispatchCourierRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default
        ) {
            Calls += 1;
            LastRequest = request;

            var response = new DispatchCourierResponse {
                Success = true,
                DispatchRef = "FLEET-1",
                AwbNumber = "KNX-20260921-ABCDEFGH",
            };

            return new AsyncUnaryCall<DispatchCourierResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );
        }
    }

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options
        );

    private static async Task<OrderEntity> SeedOrder(OrderDbContext db) {
        var order = new OrderEntity {
            OrderNumber = "ORD-DISPATCH-1",
            CustomerPrincipalId = Customer,
            Status = DomainStatus.PAID,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private sealed class RecordingWarehouse : IFulfillmentClient {
        public string? LastAwb { get; private set; }
        public int AwbCalls { get; private set; }

        public Task<FulfillmentCreated> CreateTaskAsync(
            string merchantPrincipalId, string orderNumber, IReadOnlyList<FulfillmentLine> lines
        ) => throw new NotSupportedException("not used by these tests");

        public Task<StepResult> CancelTaskAsync(string merchantPrincipalId, string fulfillmentTaskId)
            => throw new NotSupportedException("not used by these tests");

        public Task<StepResult> RecordCourierAwbAsync(
            string merchantPrincipalId, string fulfillmentTaskId, string orderNumber, string awbNumber
        ) {
            AwbCalls += 1;
            LastAwb = awbNumber;
            return Task.FromResult(StepResult.Ok());
        }
    }

    private static FulfillmentPackedHandler NewHandler(
        OrderDbContext db, RecordingCourier courier, IAddressDirectory directory,
        IFulfillmentClient? warehouse = null
    ) => new(db, courier, directory, warehouse ?? new RecordingWarehouse(),
             NullLogger<FulfillmentPackedHandler>.Instance);

    [Fact]
    public async Task TheTrackingNumberTheFleetIssuedReachesTheWarehouse() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var warehouse = new RecordingWarehouse();
        var directory = new StubDirectory(new MapPoint(-6.1754, 106.8272), new MapPoint(-6.2088, 106.8456));

        await NewHandler(db, new RecordingCourier(), directory, warehouse)
            .HandleAsync(Merchant, "ORD-DISPATCH-1", "TASK-1");

        Assert.Equal(1, warehouse.AwbCalls);
        Assert.Equal("KNX-20260921-ABCDEFGH", warehouse.LastAwb);
    }

    [Fact]
    public async Task NoDispatchMeansNothingIsSentToTheWarehouse() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var warehouse = new RecordingWarehouse();
        var directory = new StubDirectory(null, new MapPoint(-6.2088, 106.8456));

        await NewHandler(db, new RecordingCourier(), directory, warehouse)
            .HandleAsync(Merchant, "ORD-DISPATCH-1", "TASK-1");

        Assert.Equal(0, warehouse.AwbCalls);
    }

    [Fact]
    public async Task BothPointsAreSentToTheFleet() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var courier = new RecordingCourier();
        var directory = new StubDirectory(new MapPoint(-6.1754, 106.8272), new MapPoint(-6.2088, 106.8456));

        await NewHandler(db, courier, directory).HandleAsync(Merchant, "ORD-DISPATCH-1", "TASK-1");

        Assert.Equal(1, courier.Calls);
        Assert.Equal(-6.1754, courier.LastRequest!.PickupPoint.Latitude);
        Assert.Equal(106.8272, courier.LastRequest.PickupPoint.Longitude);
        Assert.Equal(-6.2088, courier.LastRequest.DeliveryPoint.Latitude);
        Assert.Equal(106.8456, courier.LastRequest.DeliveryPoint.Longitude);
    }

    [Fact]
    public async Task NoCourierIsDispatchedWhenTheMerchantHasNoPoint() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var courier = new RecordingCourier();
        var directory = new StubDirectory(null, new MapPoint(-6.2088, 106.8456));

        var outcome = await NewHandler(db, courier, directory)
            .HandleAsync(Merchant, "ORD-DISPATCH-1", "TASK-1");

        Assert.Equal(0, courier.Calls);
        Assert.Equal(string.Empty, outcome.DispatchRef);
    }

    [Fact]
    public async Task NoCourierIsDispatchedWhenTheCustomerHasNoPoint() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var courier = new RecordingCourier();
        var directory = new StubDirectory(new MapPoint(-6.1754, 106.8272), null);

        await NewHandler(db, courier, directory).HandleAsync(Merchant, "ORD-DISPATCH-1", "TASK-1");

        Assert.Equal(0, courier.Calls);
    }

    [Fact]
    public async Task ThePackedStatusStandsEvenWhenNoCourierCanBeDispatched() {
        using var db = NewDbContext();
        await SeedOrder(db);
        var directory = new StubDirectory(null, null);

        var outcome = await NewHandler(db, new RecordingCourier(), directory)
            .HandleAsync(Merchant, "ORD-DISPATCH-1", "TASK-1");

        Assert.True(outcome.Found);

        var order = await db.Orders.SingleAsync();
        Assert.Equal(DomainStatus.PROCESSING_FULFILLMENT, order.Status);
    }
}
