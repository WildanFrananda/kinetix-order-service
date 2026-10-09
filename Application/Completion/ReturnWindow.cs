using System.Globalization;

namespace Kinetix.OrderService.Application.Completion;

public sealed class ReturnWindow {
    public const string Setting = "KINETIX_RETURN_WINDOW_DAYS";

    public ReturnWindow(int days) {
        if (days < 1) {
            throw new ArgumentOutOfRangeException(nameof(days), days, "a return window is at least one day");
        }

        Length = TimeSpan.FromDays(days);
    }

    public TimeSpan Length { get; }

    public DateTime ClosesAt(DateTime deliveredAt) => deliveredAt + Length;

    public static ReturnWindow FromConfiguration(IConfiguration configuration) {
        var raw = configuration[Setting]
            ?? throw new InvalidOperationException($"{Setting} is required and has no default.");

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 1) {
            throw new InvalidOperationException(
                $"{Setting} must be a whole number of days, at least one; it is '{raw}'."
            );
        }

        return new ReturnWindow(days);
    }
}
