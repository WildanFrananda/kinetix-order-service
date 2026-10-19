using System.Globalization;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public sealed class GeocodeDeadline {
    public const string Setting = "KINETIX_GEOCODE_DEADLINE_SECONDS";

    public GeocodeDeadline(int seconds) {
        if (seconds < 1) {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "a geocode deadline is at least one second");
        }

        Length = TimeSpan.FromSeconds(seconds);
    }

    public TimeSpan Length { get; }

    public static GeocodeDeadline FromConfiguration(IConfiguration configuration) {
        var raw = configuration[Setting]
            ?? throw new InvalidOperationException($"{Setting} is required and has no default.");

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 1) {
            throw new InvalidOperationException(
                $"{Setting} must be a whole number of seconds, at least one; it is '{raw}'."
            );
        }

        return new GeocodeDeadline(seconds);
    }
}
