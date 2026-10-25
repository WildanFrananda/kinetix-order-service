using System.Globalization;

namespace Kinetix.OrderService.Infrastructure.Configuration;

public static class RequiredSetting {
    private const double MaxSeconds = int.MaxValue;

    public static string Text(IConfiguration configuration, string key) {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) {
            throw new InvalidOperationException($"{key} is required and has no default; it is unset or empty.");
        }

        return raw;
    }

    public static Uri GrpcUrl(IConfiguration configuration, string key) {
        var raw = Text(configuration, key);

        var parsed = Uri.TryCreate(raw, UriKind.Absolute, out var url);
        if (!parsed || url is null || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)) {
            throw new InvalidOperationException(
                $"{key} must be an http:// or https:// URL naming the peer's host and port; it is '{raw}'."
            );
        }

        return url;
    }

    public static int Port(IConfiguration configuration, string key) {
        var raw = Text(configuration, key);

        var parsed = int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port);
        if (!parsed || port is < 1 or > 65535) {
            throw new InvalidOperationException($"{key} must be a port number from 1 to 65535; it is '{raw}'.");
        }

        return port;
    }

    public static int Count(IConfiguration configuration, string key) {
        var raw = Text(configuration, key);

        var parsed = int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count);
        if (!parsed || count < 1) {
            throw new InvalidOperationException($"{key} must be a whole number, at least one; it is '{raw}'.");
        }

        return count;
    }

    public static TimeSpan Seconds(IConfiguration configuration, string key) {
        var raw = Text(configuration, key);

        var parsed = double.TryParse(
            raw,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var seconds
        );
        if (!parsed || seconds <= 0 || seconds > MaxSeconds) {
            throw new InvalidOperationException($"{key} must be a positive number of seconds; it is '{raw}'.");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
