using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Kinetix.OrderService.Tests;

public sealed class CapturedLogs : ILoggerProvider {
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, object?>> _entries = new();

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Capturing(_entries);

    public void Dispose() { }

    private sealed class Capturing(ConcurrentQueue<IReadOnlyDictionary<string, object?>> entries) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal) {
                ["level"] = logLevel.ToString(),
                ["message"] = formatter(state, exception),
                ["exception"] = exception,
            };

            if (state is IEnumerable<KeyValuePair<string, object?>> pairs) {
                foreach (var pair in pairs) {
                    values[pair.Key] = pair.Value;
                }
            }

            entries.Enqueue(values);
        }
    }
}
