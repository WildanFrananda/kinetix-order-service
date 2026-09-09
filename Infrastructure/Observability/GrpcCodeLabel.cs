using Grpc.Core;

namespace Kinetix.OrderService.Infrastructure.Observability;

public static class GrpcCodeLabel {
    public const string Ok = "OK";

    public const string Unknown = "UNKNOWN";

    public static string Of(StatusCode code) {
        return code switch {
            StatusCode.OK => Ok,
            StatusCode.Cancelled => "CANCELLED",
            StatusCode.Unknown => Unknown,
            StatusCode.InvalidArgument => "INVALID_ARGUMENT",
            StatusCode.DeadlineExceeded => "DEADLINE_EXCEEDED",
            StatusCode.NotFound => "NOT_FOUND",
            StatusCode.AlreadyExists => "ALREADY_EXISTS",
            StatusCode.PermissionDenied => "PERMISSION_DENIED",
            StatusCode.ResourceExhausted => "RESOURCE_EXHAUSTED",
            StatusCode.FailedPrecondition => "FAILED_PRECONDITION",
            StatusCode.Aborted => "ABORTED",
            StatusCode.OutOfRange => "OUT_OF_RANGE",
            StatusCode.Unimplemented => "UNIMPLEMENTED",
            StatusCode.Internal => "INTERNAL",
            StatusCode.Unavailable => "UNAVAILABLE",
            StatusCode.DataLoss => "DATA_LOSS",
            StatusCode.Unauthenticated => "UNAUTHENTICATED",
            _ => Unknown,
        };
    }
}
