using Kinetix.OrderService.Application.Checkout;

namespace Kinetix.OrderService.Tests;

public static class CompensationPolicies {
    public static CompensationPolicy Standard(
        int maxAttempts = 6,
        int leaseSeconds = 60,
        int baseDelaySeconds = 30,
        int maxDelaySeconds = 30 * 60
    ) => new(maxAttempts, leaseSeconds, baseDelaySeconds, maxDelaySeconds);
}
