using Grpc.Core;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class CompensationPolicyTests {

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    public void TheBackoffDoublesAndNeverExceedsTheRungItIsOn(int attempt, int rungSeconds) {
        var policy = new CompensationPolicy();

        for (var i = 0; i < 50; i++) {
            var delay = policy.DelaySeconds(attempt);

            Assert.InRange(delay, rungSeconds / 2, rungSeconds);
        }
    }

    [Fact]
    public void TheBackoffIsCappedSoALongOutageDoesNotScheduleARetryNextWeek() {
        var policy = new CompensationPolicy(baseDelaySeconds: 30, maxDelaySeconds: 600);

        Assert.InRange(policy.DelaySeconds(20), 300, 600);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.ResourceExhausted)]
    [InlineData(StatusCode.Aborted)]
    public void AnAnswerThatCouldChangeIsWorthAnotherRound(StatusCode statusCode) {
        var kind = CompensationPolicy.Classify(new RpcException(new Status(statusCode, "not now")));

        Assert.Equal(CompensationFailureKind.Transient, kind);
    }

    [Theory]
    [InlineData(StatusCode.FailedPrecondition)]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    public void AnAnswerNoRetryCanChangeGoesStraightToAPerson(StatusCode statusCode) {
        var kind = CompensationPolicy.Classify(new RpcException(new Status(statusCode, "no")));

        Assert.Equal(CompensationFailureKind.Terminal, kind);
    }

    [Fact]
    public void AnAlreadyReleasedHoldIsTerminalEvenThoughPaymentOnlySaysSoInProse() {
        var rpc = new RpcException(new Status(
            StatusCode.Unknown,
            "escrow for order ORD-1 was already released and cannot be refunded here"));

        Assert.Equal(CompensationFailureKind.Terminal, CompensationPolicy.Classify(rpc));

        var reworded = new RpcException(new Status(StatusCode.Unknown, "escrow already paid out"));
        Assert.Equal(CompensationFailureKind.Transient, CompensationPolicy.Classify(reworded));
    }
}
