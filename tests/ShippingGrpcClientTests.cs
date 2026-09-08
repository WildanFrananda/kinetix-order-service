using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Infrastructure.Grpc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using CommonProto = global::Common.V1;
using ShippingProto = global::Shipping.V1;

namespace Kinetix.OrderService.Tests;

public class ShippingGrpcClientTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static AsyncUnaryCall<ShippingProto.EstimateShippingOptionsResponse> Answer(
        ShippingProto.EstimateShippingOptionsResponse response) =>
        new(Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { }
        );

    private static ShippingGrpcClient ClientOver(
        Mock<ShippingProto.ShippingService.ShippingServiceClient> matching
    ) => new(matching.Object, NullLogger<ShippingGrpcClient>.Instance);

    private static Mock<ShippingProto.ShippingService.ShippingServiceClient> Faulting(StatusCode code) {
        var matching = new Mock<ShippingProto.ShippingService.ShippingServiceClient>();
        matching.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<ShippingProto.EstimateShippingOptionsRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()
        )).Throws(new RpcException(new Status(code, "matching did not answer")));
        return matching;
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.Internal)]
    public async Task EveryTransportFailureRaisesRatherThanReturningAFreeQuote(StatusCode code) {
        var client = ClientOver(Faulting(code));

        var refusal = await Assert.ThrowsAsync<ShippingUnavailableException>(() =>
            client.EstimateShippingOptionsAsync(0, 0, 0, 0, 0, Merchant));

        var cause = Assert.IsType<RpcException>(refusal.InnerException);
        Assert.Equal(code, cause.StatusCode);
    }

    [Fact]
    public async Task ANonGrpcFaultAlsoRaises() {
        var matching = new Mock<ShippingProto.ShippingService.ShippingServiceClient>();
        matching.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<ShippingProto.EstimateShippingOptionsRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()
        )).Throws(new InvalidOperationException("the mesh handler has no client certificate"));

        var client = ClientOver(matching);

        await Assert.ThrowsAsync<ShippingUnavailableException>(() =>
            client.EstimateShippingOptionsAsync(0, 0, 0, 0, 0, Merchant)
        );
    }

    [Fact]
    public async Task TheMerchantPrincipalReachesTheWire() {
        var matching = new Mock<ShippingProto.ShippingService.ShippingServiceClient>();
        ShippingProto.EstimateShippingOptionsRequest? sent = null;

        matching.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<ShippingProto.EstimateShippingOptionsRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()
        )).Returns((ShippingProto.EstimateShippingOptionsRequest request, Metadata _, DateTime? __, CancellationToken ___) => {
            sent = request;
            return Answer(new ShippingProto.EstimateShippingOptionsResponse { DistanceKm = 0 });
        });

        await ClientOver(matching).EstimateShippingOptionsAsync(0, 0, 0, 0, 0, Merchant);

        Assert.NotNull(sent);
        Assert.Equal(Merchant, sent.MerchantPrincipalId);
    }

    [Fact]
    public async Task WeightCrossesAsIntegerGrams() {
        var matching = new Mock<ShippingProto.ShippingService.ShippingServiceClient>();
        ShippingProto.EstimateShippingOptionsRequest? sent = null;

        matching.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<ShippingProto.EstimateShippingOptionsRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()
        )).Returns((ShippingProto.EstimateShippingOptionsRequest request, Metadata _, DateTime? __, CancellationToken ___) => {
            sent = request;
            return Answer(new ShippingProto.EstimateShippingOptionsResponse { DistanceKm = 0 });
        });

        await ClientOver(matching).EstimateShippingOptionsAsync(0, 0, 0, 0, 12345L, Merchant);

        Assert.NotNull(sent);
        Assert.Equal(12345L, sent.TotalWeightGrams);
    }

    [Fact]
    public async Task FeesArriveAsExactMinorUnits() {
        var matching = new Mock<ShippingProto.ShippingService.ShippingServiceClient>();
        var response = new ShippingProto.EstimateShippingOptionsResponse { DistanceKm = 0 };
        response.Options.Add(new ShippingProto.CourierOption {
            ServiceTier = "KINETIX_REGULAR",
            ServiceName = "Kinetix Regular Freight",
            DistanceKm = 0,
            BaseShippingFee = new CommonProto.Money { AmountMinor = 900_000, Currency = "IDR" },
            EstimatedDeliveryTime = "1 - 3 Hari",
            IsAvailable = true,
            UnavailableReason = ""
        });

        matching.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<ShippingProto.EstimateShippingOptionsRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()
        )).Returns(Answer(response));

        var result = await ClientOver(matching).EstimateShippingOptionsAsync(0, 0, 0, 0, 0, Merchant);

        var option = Assert.Single(result.Options);
        Assert.Equal(9000m, option.BaseShippingFee);
        Assert.Null(option.UnavailableReason);
    }

    [Fact]
    public async Task AnOptionWithNoMoneyOnItArrivesUnpricedRatherThanFree() {
        var matching = new Mock<ShippingProto.ShippingService.ShippingServiceClient>();
        var response = new ShippingProto.EstimateShippingOptionsResponse { DistanceKm = 0 };
        response.Options.Add(new ShippingProto.CourierOption {
            ServiceTier = "KINETIX_REGULAR",
            ServiceName = "Kinetix Regular Freight",
            DistanceKm = 0,
            EstimatedDeliveryTime = "1 - 3 Hari",
            IsAvailable = true,
            UnavailableReason = ""
        });

        matching.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<ShippingProto.EstimateShippingOptionsRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()
        )).Returns(Answer(response));

        var result = await ClientOver(matching).EstimateShippingOptionsAsync(0, 0, 0, 0, 0, Merchant);

        var option = Assert.Single(result.Options);
        Assert.Null(option.BaseShippingFee);
        Assert.NotEqual(0m, option.BaseShippingFee);
    }

    [Fact]
    public async Task AnAvailableOptionQuotedInAnotherCurrencyIsRefusedRatherThanReadAsRupiah() {
        var matching = new Mock<ShippingProto.ShippingService.ShippingServiceClient>();
        var response = new ShippingProto.EstimateShippingOptionsResponse { DistanceKm = 0 };
        response.Options.Add(new ShippingProto.CourierOption {
            ServiceTier = "KINETIX_REGULAR",
            ServiceName = "Kinetix Regular Freight",
            DistanceKm = 0,
            BaseShippingFee = new CommonProto.Money { AmountMinor = 900_000, Currency = "USD" },
            EstimatedDeliveryTime = "1 - 3 Hari",
            IsAvailable = true,
            UnavailableReason = ""
        });

        matching.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<ShippingProto.EstimateShippingOptionsRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()
        )).Returns(Answer(response));

        var refusal = await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            ClientOver(matching).EstimateShippingOptionsAsync(0, 0, 0, 0, 0, Merchant)
        );

        Assert.Contains("USD", refusal.Fault);
    }

    [Fact]
    public async Task AnUnreadableAnswerIsNotReportedAsMatchingBeingDown() {
        var matching = new Mock<ShippingProto.ShippingService.ShippingServiceClient>();
        var response = new ShippingProto.EstimateShippingOptionsResponse { DistanceKm = 0 };
        response.Options.Add(new ShippingProto.CourierOption {
            ServiceTier = "KINETIX_REGULAR",
            BaseShippingFee = new CommonProto.Money { AmountMinor = 900_000, Currency = "EUR" },
            IsAvailable = true,
        });

        matching.Setup(c => c.EstimateShippingOptionsAsync(
            It.IsAny<ShippingProto.EstimateShippingOptionsRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()
        )).Returns(Answer(response));

        await Assert.ThrowsAsync<ShippingQuoteMalformedException>(() =>
            ClientOver(matching).EstimateShippingOptionsAsync(0, 0, 0, 0, 0, Merchant)
        );
    }
}
