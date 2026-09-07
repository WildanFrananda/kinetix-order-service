namespace Kinetix.OrderService.Infrastructure.Observability;

public static class LogLevelSetting {
    public static LogLevel? FromEnvironment(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch {
            "trace" or "verbose" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" or "fatal" => LogLevel.Critical,
            "none" => LogLevel.None,
            _ => throw new InvalidOperationException(
                $"LOG_LEVEL is '{value}', which is not a level. Use one of: "
                    + "trace, debug, info, warn, error, critical, none.")
        };
    }
}
