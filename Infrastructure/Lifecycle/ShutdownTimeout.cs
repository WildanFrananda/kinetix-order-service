using System.Globalization;

namespace Kinetix.OrderService.Infrastructure.Lifecycle;

public static class ShutdownTimeout {
    public const string Setting = "KINETIX_SHUTDOWN_TIMEOUT_SECONDS";

    public static TimeSpan FromEnvironment(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException(
                $"{Setting} is required and has no default; it is unset or empty. Set it a few seconds "
                    + "inside the container's stop grace period."
            );
        }

        var parsed = double.TryParse(
            value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds
        );

        if (!parsed || seconds <= 0) {
            throw new InvalidOperationException(
                $"{Setting} is '{value}', which is not a positive number of "
                    + "seconds. A shutdown budget that is silently ignored is only visible as a "
                    + "SIGKILL in production."
            );
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
