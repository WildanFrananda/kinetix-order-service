using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Infrastructure.Grpc;
using Moq;
using IdentityProto = global::Identity.V1;

namespace Kinetix.OrderService.Tests;

public class IdentityMerchantStandingTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static IdentityMerchantStanding StandingFaulting(Exception fault) {
        var identity = new Mock<IdentityProto.IdentityService.IdentityServiceClient>();
        identity.Setup(c => c.GetMerchantInfoAsync(
            It.IsAny<IdentityProto.GetMerchantInfoRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()
        )).Throws(fault);

        return new IdentityMerchantStanding(identity.Object);
    }

    [Fact]
    public async Task IdentityThatDoesNotAnswerLeavesTheStandingUnknown() {
        var fault = new RpcException(new Status(StatusCode.Unavailable, "down"));

        await Assert.ThrowsAsync<MerchantStandingUnknownException>(() =>
            StandingFaulting(fault).MaySellAsync(Merchant)
        );
    }

    [Fact]
    public async Task AFaultBeforeIdentityIsReachedLeavesTheStandingUnknownToo() {
        var fault = new InvalidOperationException("the mesh handler has no client certificate");

        await Assert.ThrowsAsync<MerchantStandingUnknownException>(() =>
            StandingFaulting(fault).MaySellAsync(Merchant)
        );
    }
}
