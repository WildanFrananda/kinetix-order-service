using Grpc.Core;
using Kinetix.OrderService.Application.Faults;

namespace Kinetix.OrderService.Tests;

public class FailureReasonTests {
    private const string Transport =
        "Error starting gRPC call. HttpRequestException: Connection refused (kinetix-payment-service:50056)";

    [Theory]
    [InlineData(StatusCode.Unavailable, "PAYMENT_UNAVAILABLE")]
    [InlineData(StatusCode.DeadlineExceeded, "PAYMENT_TIMED_OUT")]
    [InlineData(StatusCode.FailedPrecondition, "PAYMENT_REFUSED")]
    [InlineData(StatusCode.InvalidArgument, "PAYMENT_REFUSED")]
    [InlineData(StatusCode.NotFound, "PAYMENT_REFUSED")]
    [InlineData(StatusCode.PermissionDenied, "PAYMENT_REFUSED")]
    [InlineData(StatusCode.Unauthenticated, "PAYMENT_REFUSED")]
    [InlineData(StatusCode.Aborted, "PAYMENT_FAILED")]
    [InlineData(StatusCode.Internal, "PAYMENT_FAILED")]
    [InlineData(StatusCode.Unknown, "PAYMENT_FAILED")]
    [InlineData(StatusCode.Cancelled, "PAYMENT_FAILED")]
    [InlineData(StatusCode.ResourceExhausted, "PAYMENT_FAILED")]
    public void AStatusIsNamedByWhatItSaysAndNeverByItsText(StatusCode code, string reason) {
        var named = FailureReason.Of(CalledService.Payment, new RpcException(new Status(code, Transport)));

        Assert.Equal(reason, named);
    }

    [Fact]
    public void APaymentThatSaysItAlreadyReleasedIsARefusal() {
        var named = FailureReason.Of(
            CalledService.Payment,
            new RpcException(new Status(StatusCode.Unknown, "escrow already released"))
        );

        Assert.Equal("PAYMENT_REFUSED", named);
    }

    [Theory]
    [InlineData(CalledService.Identity, "IDENTITY_UNAVAILABLE")]
    [InlineData(CalledService.Matching, "MATCHING_UNAVAILABLE")]
    [InlineData(CalledService.Warehouse, "WAREHOUSE_UNAVAILABLE")]
    public void TheServiceThatWasCalledIsNamed(CalledService service, string reason) {
        var named = FailureReason.Of(service, new RpcException(new Status(StatusCode.Unavailable, Transport)));

        Assert.Equal(reason, named);
    }

    [Fact]
    public void AFailureThatIsNotAnAnswerIsUnexpected() {
        var named = FailureReason.Of(
            CalledService.Payment,
            new InvalidOperationException("could not read /pki/tls.crt")
        );

        Assert.Equal(FailureReason.Unexpected, named);
    }
}
