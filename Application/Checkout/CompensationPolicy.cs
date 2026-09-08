using Grpc.Core;
using Kinetix.OrderService.Domain.Entities;

namespace Kinetix.OrderService.Application.Services;

public class CompensationPolicy {
    private const int DefaultMaxAttempts = 6;
    private const int DefaultLeaseSeconds = 60;
    private const int DefaultBaseDelaySeconds = 30;
    private const int DefaultMaxDelaySeconds = 30 * 60;

    public CompensationPolicy(
        int maxAttempts = DefaultMaxAttempts,
        int leaseSeconds = DefaultLeaseSeconds,
        int baseDelaySeconds = DefaultBaseDelaySeconds,
        int maxDelaySeconds = DefaultMaxDelaySeconds
    ) {
        MaxAttempts = Math.Max(1, maxAttempts);
        LeaseSeconds = Math.Max(5, leaseSeconds);
        BaseDelaySeconds = Math.Max(1, baseDelaySeconds);
        MaxDelaySeconds = Math.Max(BaseDelaySeconds, maxDelaySeconds);
    }

    public int MaxAttempts { get; }

    public int LeaseSeconds { get; }

    public int BaseDelaySeconds { get; }

    public int MaxDelaySeconds { get; }

    public static CompensationPolicy FromConfiguration(IConfiguration configuration) => new(
        Read(configuration, "KINETIX_SAGA_MAX_COMPENSATION_ATTEMPTS", DefaultMaxAttempts),
        Read(configuration, "KINETIX_SAGA_LEASE_SECONDS", DefaultLeaseSeconds),
        Read(configuration, "KINETIX_SAGA_BACKOFF_BASE_SECONDS", DefaultBaseDelaySeconds),
        Read(configuration, "KINETIX_SAGA_BACKOFF_MAX_SECONDS", DefaultMaxDelaySeconds)
    );

    public int DelaySeconds(int attemptsSoFar) {
        var exponent = Math.Clamp(attemptsSoFar - 1, 0, 20);
        var uncapped = (double)BaseDelaySeconds * Math.Pow(2, exponent);
        var capped = Math.Min(uncapped, MaxDelaySeconds);
        var jittered = capped * (0.5 + (Random.Shared.NextDouble() / 2));
        return Math.Max(1, (int)Math.Round(jittered));
    }

    public static CompensationFailureKind Classify(Exception exception) => exception switch {
        RpcException rpc => Classify(rpc),
        OperationCanceledException => CompensationFailureKind.Transient,
        _ => CompensationFailureKind.Transient,
    };

    public static CompensationFailureKind Classify(RpcException rpc) => rpc.StatusCode switch {
        StatusCode.FailedPrecondition
            or StatusCode.InvalidArgument
            or StatusCode.NotFound
            or StatusCode.PermissionDenied
            or StatusCode.Unauthenticated => CompensationFailureKind.Terminal,
        StatusCode.Unknown when LooksAlreadyReleased(rpc.Status.Detail) => CompensationFailureKind.Terminal,
        _ => CompensationFailureKind.Transient,
    };

    public static bool LooksAlreadyReleased(string? detail) =>
        detail is not null
        && detail.Contains("already released", StringComparison.OrdinalIgnoreCase);

    private static int Read(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;
}
