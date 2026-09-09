using System.Globalization;

namespace Kinetix.OrderService.Infrastructure.Lifecycle;

public static class ShutdownTimeout {
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(25);

    public static TimeSpan FromEnvironment(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return Default;
        }

        var parsed = double.TryParse(
            value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds
        );

        if (!parsed || seconds <= 0) {
            throw new InvalidOperationException(
                $"KINETIX_SHUTDOWN_TIMEOUT_SECONDS is '{value}', which is not a positive number of "
                    + "seconds. A shutdown budget that is silently ignored is only visible as a "
                    + "SIGKILL in production."
            );
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
