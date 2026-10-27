using Common.V1;
using Fleet.V1;
using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Delivery;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Fulfillment;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Infrastructure.Background;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using DomainStatus = Kinetix.OrderService.Domain.Enums.OrderStatus;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class CourierDispatchTests {
    private const string Merchant = "11111111-1111-1111-1111-111111111111";
    private const string Customer = "22222222-2222-2222-2222-222222222222";
    private const string OrderNumber = "ORD-DISPATCH-RETRY";
    private const string Awb = "KNX-20261011-ABCDEFGH";

    private static readonly MapPoint Store = new(-6.1754, 106.8272);
    private static readonly MapPoint Home = new(-6.2088, 106.8456);

    private sealed class ScriptedDirectory : IAddressDirectory {
        public Queue<bool> Answers { get; } = new();

        public Task<MapPoint?> PickupPointAsync(string merchantPrincipalId) => Answer(merchantPrincipalId, Store);

        public int DeliveryPointCalls { get; private set; }

        public Task<MapPoint?> DeliveryPointAsync(string customerPrincipalId) {
            DeliveryPointCalls += 1;
            return Answer(customerPrincipalId, Home);
        }

        private Task<MapPoint?> Answer(string principal, MapPoint point) {
            if (Answers.Count > 0 && !Answers.Peek()) {
                Answers.Dequeue();
                throw new AddressUnknownException(
                    principal,
                    new RpcException(new Status(
                        StatusCode.Unavailable,
                        "Error starting gRPC call. HttpRequestException: "
                      + "Connection refused (kinetix-identity-service:50052)"
                    ))
                );
            }

            return Task.FromResult<MapPoint?>(point);
        }
    }

    private sealed class ScriptedCourier : CourierTelemetryService.CourierTelemetryServiceClient {
        public Queue<Func<DispatchCourierResponse>> Answers { get; } = new();
        public int Calls { get; private set; }
        public DispatchCourierRequest? LastRequest { get; private set; }

        public override AsyncUnaryCall<DispatchCourierResponse> DispatchCourierAsync(
            DispatchCourierRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default
        ) {
            Calls += 1;
            LastRequest = request;
            var answer = Answers.Count > 0 ? Answers.Dequeue() : Dispatched;

            return new AsyncUnaryCall<DispatchCourierResponse>(
                Task.FromResult(answer()),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );
        }
    }

    private sealed class ScriptedWarehouse : IFulfillmentClient {
        public Queue<Func<StepResult>> Answers { get; } = new();
        public int AwbCalls { get; private set; }
        public string? LastAwb { get; private set; }

        public Task<FulfillmentCreated> CreateTaskAsync(
            string merchantPrincipalId,
            string orderNumber,
            IReadOnlyList<FulfillmentLine> lines
        ) => throw new NotSupportedException("not used by these tests");

        public Task<StepResult> CancelTaskAsync(string merchantPrincipalId, string fulfillmentTaskId) =>
            throw new NotSupportedException("not used by these tests");

        public Task<StepResult> RecordCourierAwbAsync(
            string merchantPrincipalId,
            string fulfillmentTaskId,
            string orderNumber,
            string awbNumber
        ) {
            AwbCalls += 1;
            LastAwb = awbNumber;
            var answer = Answers.Count > 0 ? Answers.Dequeue() : StepResult.Ok;
            return Task.FromResult(answer());
        }
    }

    private static DispatchCourierResponse Dispatched() =>
        new() { Success = true, DispatchRef = "DISP-7", AwbNumber = Awb };

    private static Func<DispatchCourierResponse> Refused(string code) =>
        () => new DispatchCourierResponse {
            Success = false,
            Error = new ErrorDetail { ErrorCode = code, Message = code.ToLowerInvariant() },
        };

    private static Func<DispatchCourierResponse> Unreachable() =>
        () => throw new RpcException(new Status(
            StatusCode.Unavailable,
            "Error starting gRPC call. HttpRequestException: Connection refused (kinetix-matching-service:50053)"
        ));

    private static Func<StepResult> WarehouseUnreachable() =>
        () => throw new RpcException(new Status(
            StatusCode.Unavailable,
            "Error starting gRPC call. HttpRequestException: Connection refused (kinetix-warehouse-service:50051)"
        ));

    private static Func<DispatchCourierResponse> MeshRefusing() =>
        () => throw new InvalidOperationException("the mesh handler has no client certificate");

    private static Func<StepResult> WarehouseMeshRefusing() =>
        () => throw new InvalidOperationException("the mesh handler has no client certificate");

    private sealed class World {
        public string Database { get; } = Guid.NewGuid().ToString();
        public ScriptedDirectory Directory { get; } = new();
        public ScriptedCourier Courier { get; } = new();
        public ScriptedWarehouse Warehouse { get; } = new();

        public OrderDbContext Db() =>
            new(new DbContextOptionsBuilder<OrderDbContext>().UseInMemoryDatabase(Database).Options);

        public CourierDispatcher Dispatcher() =>
            new(Courier, Directory, Warehouse, NullLogger<CourierDispatcher>.Instance);

        public async Task<FulfillmentPackedOutcome> Packed() {
            using var db = Db();
            return await new FulfillmentPackedHandler(db, Dispatcher())
                .HandleAsync(Merchant, OrderNumber, "TASK-1");
        }

        public async Task Sweep(bool dueNow = true) {
            if (dueNow) {
                using var db = Db();
                foreach (var row in db.CourierDispatches.Where(d => d.NextAttemptAt != null)) {
                    row.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
                }

                await db.SaveChangesAsync();
            }

            var services = new ServiceCollection()
                .AddDbContext<OrderDbContext>(options => options.UseInMemoryDatabase(Database))
                .AddSingleton<ICourierDispatcher>(Dispatcher())
                .BuildServiceProvider();

            await new CourierDispatchSweeper(
                services.GetRequiredService<IServiceScopeFactory>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["KINETIX_DISPATCH_SWEEP_BATCH"] = "20" })
                    .Build(),
                NullLogger<CourierDispatchSweeper>.Instance
            ).SweepAsync(CancellationToken.None);
        }

        public async Task<Domain.Entities.CourierDispatch> Row() {
            using var db = Db();
            return await db.CourierDispatches.SingleAsync();
        }

        public async Task SetStatus(DomainStatus status) {
            using var db = Db();
            (await db.Orders.SingleAsync()).Status = status;
            await db.SaveChangesAsync();
        }
    }

    private static async Task<World> PaidOrder(MapPoint? dropOff = null, long? parcelWeightGrams = null) {
        var world = new World();
        using var db = world.Db();
        db.Orders.Add(new OrderEntity {
            OrderNumber = OrderNumber,
            CustomerPrincipalId = Customer,
            MerchantPrincipalId = Merchant,
            Status = DomainStatus.PAID,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "0812",
            DeliveryLatitude = dropOff?.Latitude,
            DeliveryLongitude = dropOff?.Longitude,
            ParcelWeightGrams = parcelWeightGrams,
        });

        await db.SaveChangesAsync();
        return world;
    }

    [Fact]
    public async Task APackedOrderIsDispatchedAndItsTrackingNumberReachesTheWarehouse() {
        var world = await PaidOrder();

        var outcome = await world.Packed();

        var row = await world.Row();
        Assert.Equal("DISP-7", outcome.DispatchRef);
        Assert.NotNull(row.DispatchedAt);
        Assert.NotNull(row.AwbRecordedAt);
        Assert.Null(row.NextAttemptAt);
        Assert.Equal(Awb, world.Warehouse.LastAwb);
    }

    [Fact]
    public async Task TheCourierPaidIsTheOneWhoReportedTheDelivery_NotTheOneFirstAssigned() {
        const string firstAssigned = "33333333-3333-3333-3333-333333333333";
        const string delivering = "44444444-4444-4444-4444-444444444444";
        var world = await PaidOrder();
        world.Courier.Answers.Enqueue(() => new DispatchCourierResponse {
            Success = true,
            DispatchRef = "DISP-7",
            AwbNumber = Awb,
            AssignedDriverPrincipalId = firstAssigned,
        });
        await world.Packed();

        var escrow = new Mock<IEscrowClient>();
        escrow.Setup(e => e.SettleShippingFeeAsync(OrderNumber, It.IsAny<string>())).ReturnsAsync(StepResult.Ok());
        using (var db = world.Db()) {
            await new OrderDeliveredHandler(db, escrow.Object, NullLogger<OrderDeliveredHandler>.Instance)
                .HandleAsync(OrderNumber, delivering, DateTime.UtcNow);
        }

        using var read = world.Db();
        Assert.Equal(delivering, (await read.ShippingSettlements.SingleAsync()).DriverPrincipalId);
        escrow.Verify(e => e.SettleShippingFeeAsync(OrderNumber, delivering), Times.Once);
        escrow.Verify(e => e.SettleShippingFeeAsync(OrderNumber, firstAssigned), Times.Never);
    }

    [Fact]
    public async Task AnOrderThatRecordedItsDropOffIsDeliveredThere_WithItsParcelsWeight() {
        var office = new MapPoint(-6.1900, 106.8230);
        var world = await PaidOrder(office, 3850);

        await world.Packed();

        var request = Assert.IsType<DispatchCourierRequest>(world.Courier.LastRequest);
        Assert.Equal(office.Latitude, request.DeliveryPoint.Latitude);
        Assert.Equal(office.Longitude, request.DeliveryPoint.Longitude);
        Assert.Equal(3850L, request.ParcelWeightGrams);
        Assert.Equal(0, world.Directory.DeliveryPointCalls);
    }

    [Fact]
    public async Task AnOrderPlacedBeforeDropOffsWereRecordedGoesToTheBuyersProfilePoint_WithNoWeightStated() {
        var world = await PaidOrder();

        await world.Packed();

        var request = Assert.IsType<DispatchCourierRequest>(world.Courier.LastRequest);
        Assert.Equal(Home.Latitude, request.DeliveryPoint.Latitude);
        Assert.Equal(0L, request.ParcelWeightGrams);
        Assert.Equal(1, world.Directory.DeliveryPointCalls);
    }

    [Fact]
    public async Task WhenIdentityCannotBeAskedTheDispatchIsOwedNotForgotten() {
        var world = await PaidOrder();
        world.Directory.Answers.Enqueue(false);

        var outcome = await world.Packed();

        var row = await world.Row();
        Assert.True(outcome.Found);
        Assert.Equal(string.Empty, outcome.DispatchRef);
        Assert.Equal(0, world.Courier.Calls);
        Assert.Null(row.DispatchedAt);
        Assert.NotNull(row.NextAttemptAt);
        Assert.Equal("IDENTITY_UNAVAILABLE", row.LastError);

        await world.Sweep();

        row = await world.Row();
        Assert.Equal(1, world.Courier.Calls);
        Assert.NotNull(row.DispatchedAt);
        Assert.NotNull(row.AwbRecordedAt);
        Assert.Null(row.NextAttemptAt);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task WhenTheFleetCannotBeReachedTheDispatchIsRetried() {
        var world = await PaidOrder();
        world.Courier.Answers.Enqueue(Unreachable());

        await world.Packed();

        Assert.Equal("MATCHING_UNAVAILABLE", (await world.Row()).LastError);

        await world.Sweep();

        Assert.NotNull((await world.Row()).DispatchedAt);
        Assert.Equal(2, world.Courier.Calls);
    }

    [Fact]
    public async Task AFleetTheMeshCannotReachIsRetriedLikeAnOutage() {
        var world = await PaidOrder();
        world.Courier.Answers.Enqueue(MeshRefusing());

        await world.Packed();

        var row = await world.Row();
        Assert.Equal("UNEXPECTED_FAILURE", row.LastError);
        Assert.NotNull(row.NextAttemptAt);

        await world.Sweep();

        Assert.NotNull((await world.Row()).DispatchedAt);
    }

    [Fact]
    public async Task AWarehouseTheMeshCannotReachIsToldLaterWithoutASecondDispatch() {
        var world = await PaidOrder();
        world.Warehouse.Answers.Enqueue(WarehouseMeshRefusing());

        await world.Packed();

        var row = await world.Row();
        Assert.NotNull(row.DispatchedAt);
        Assert.Null(row.AwbRecordedAt);
        Assert.Equal("UNEXPECTED_FAILURE", row.LastError);

        await world.Sweep();

        Assert.Equal(1, world.Courier.Calls);
        Assert.NotNull((await world.Row()).AwbRecordedAt);
    }

    [Fact]
    public async Task WhenNoCourierIsFreeTheDispatchIsAskedAgainForItsTrackingNumber() {
        var world = await PaidOrder();
        world.Courier.Answers.Enqueue(Refused("NO_DRIVER_AVAILABLE"));

        await world.Packed();

        var row = await world.Row();
        Assert.NotNull(row.NextAttemptAt);
        Assert.Contains("NO_DRIVER_AVAILABLE", row.LastError);

        await world.Sweep();

        Assert.Equal(Awb, world.Warehouse.LastAwb);
        Assert.Null((await world.Row()).NextAttemptAt);
    }

    [Fact]
    public async Task AFleetThatSaysTheJobIsFinishedStopsTheRetry() {
        var world = await PaidOrder();
        world.Courier.Answers.Enqueue(Refused("ORDER_ALREADY_FINISHED"));

        await world.Packed();

        var row = await world.Row();
        Assert.Null(row.NextAttemptAt);
        Assert.Null(row.DispatchedAt);
        Assert.Contains("ORDER_ALREADY_FINISHED", row.LastError);
    }

    [Fact]
    public async Task AWarehouseThatCannotBeToldIsToldLaterWithoutASecondDispatch() {
        var world = await PaidOrder();
        world.Warehouse.Answers.Enqueue(WarehouseUnreachable());

        await world.Packed();

        var row = await world.Row();
        Assert.NotNull(row.DispatchedAt);
        Assert.Null(row.AwbRecordedAt);
        Assert.NotNull(row.NextAttemptAt);
        Assert.Equal("WAREHOUSE_UNAVAILABLE", row.LastError);

        await world.Sweep();

        row = await world.Row();
        Assert.Equal(1, world.Courier.Calls);
        Assert.Equal(2, world.Warehouse.AwbCalls);
        Assert.NotNull(row.AwbRecordedAt);
        Assert.Null(row.NextAttemptAt);
    }

    [Fact]
    public async Task AWarehouseThatRefusesTheTrackingNumberStopsTheRetry() {
        var world = await PaidOrder();
        world.Warehouse.Answers.Enqueue(() => StepResult.Refused("the task was cancelled"));

        await world.Packed();

        var row = await world.Row();
        Assert.NotNull(row.DispatchedAt);
        Assert.Null(row.AwbRecordedAt);
        Assert.Null(row.NextAttemptAt);
        Assert.Contains("the task was cancelled", row.LastError);
    }

    [Fact]
    public async Task AnOrderCancelledWhileItWaitedIsNotDispatched() {
        var world = await PaidOrder();
        world.Courier.Answers.Enqueue(Unreachable());
        await world.Packed();
        await world.SetStatus(DomainStatus.CANCELLED);

        await world.Sweep();

        var row = await world.Row();
        Assert.Equal(1, world.Courier.Calls);
        Assert.Null(row.NextAttemptAt);
        Assert.Contains("CANCELLED", row.LastError);
    }

    [Fact]
    public async Task ARepeatedPackedReportAnswersWithTheDispatchAlreadyMade() {
        var world = await PaidOrder();
        await world.Packed();

        var again = await world.Packed();

        Assert.Equal(1, world.Courier.Calls);
        Assert.Equal("DISP-7", again.DispatchRef);
        Assert.Equal(1, (await world.Row()).Attempts);
    }

    [Fact]
    public async Task ADispatchThatIsNotYetDueIsLeftAlone() {
        var world = await PaidOrder();
        world.Courier.Answers.Enqueue(Unreachable());
        await world.Packed();

        await world.Sweep(dueNow: false);

        Assert.Equal(1, world.Courier.Calls);
    }

    [Fact]
    public async Task EachRetryWaitsLongerUpToAnHour() {
        var world = await PaidOrder();
        for (var i = 0; i < 3; i++) {
            world.Courier.Answers.Enqueue(Unreachable());
        }

        await world.Packed();
        await world.Sweep();
        await world.Sweep();

        var row = await world.Row();
        Assert.Equal(3, row.Attempts);
        var wait = row.NextAttemptAt!.Value - DateTime.UtcNow;
        Assert.InRange(wait.TotalMinutes, 2.9, 3.0);
    }
}
