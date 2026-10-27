using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Application.Faults;

public static class FailureReason {
    public const string Unexpected = "UNEXPECTED_FAILURE";

    public static string Of(CalledService service, Exception failure) => failure switch {
        RpcException rpc when CompensationPolicy.Classify(rpc) is CompensationFailureKind.Terminal =>
            Named(service, "REFUSED"),
        RpcException { StatusCode: StatusCode.Unavailable } => Named(service, "UNAVAILABLE"),
        RpcException { StatusCode: StatusCode.DeadlineExceeded } => Named(service, "TIMED_OUT"),
        RpcException => Named(service, "FAILED"),
        _ => Unexpected,
    };

    private static string Named(CalledService service, string outcome) =>
        $"{service.ToString().ToUpperInvariant()}_{outcome}";
}
