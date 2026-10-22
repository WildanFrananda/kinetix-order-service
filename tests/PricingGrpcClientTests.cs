using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Infrastructure.Grpc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PricingProto = global::Pricing.V1;

namespace Kinetix.OrderService.Tests;

public class PricingGrpcClientTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly IReadOnlyList<PriceLine> OneLine = [new PriceLine("PRODUCT-01", null, 100000m, 1)];

    private static readonly ShippingJourney Regular = new("KINETIX_REGULAR", 4.2, 500);

    private static PricingGrpcClient ClientFaulting(StatusCode code) {
        var pricing = new Mock<PricingProto.PricingService.PricingServiceClient>();
        var fault = new RpcException(new Status(code, "CalculatePrice: item PRODUCT-01 has quantity 0"));

        pricing.Setup(c => c.CalculatePriceAsync(
            It.IsAny<PricingProto.CalculatePriceRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()
        )).Throws(fault);
        pricing.Setup(c => c.QuoteShippingAsync(
            It.IsAny<PricingProto.QuoteShippingRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()
        )).Throws(fault);

        return new PricingGrpcClient(pricing.Object, NullLogger<PricingGrpcClient>.Instance);
    }

    [Theory]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.FailedPrecondition)]
    [InlineData(StatusCode.NotFound)]
    public async Task ARefusalPricingGaveIsNotReportedAsAnOutage(StatusCode code) {
        var client = ClientFaulting(code);

        var price = await Assert.ThrowsAsync<PricingRefusedException>(() =>
            client.CalculatePriceAsync(null, OneLine, Regular, Merchant)
        );
        var quote = await Assert.ThrowsAsync<PricingRefusedException>(() => client.QuoteShippingAsync([Regular]));

        Assert.Contains("quantity 0", price.Reason);
        Assert.Contains("quantity 0", quote.Reason);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Unauthenticated)]
    public async Task AnAnswerPricingCouldNotGiveIsAnOutage(StatusCode code) {
        var client = ClientFaulting(code);

        await Assert.ThrowsAsync<PricingUnavailableException>(() =>
            client.CalculatePriceAsync(null, OneLine, Regular, Merchant)
        );
        await Assert.ThrowsAsync<PricingUnavailableException>(() => client.QuoteShippingAsync([Regular]));
    }
}
