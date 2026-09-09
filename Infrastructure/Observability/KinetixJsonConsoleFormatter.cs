using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Kinetix.OrderService.Infrastructure.Observability;

public sealed class KinetixJsonConsoleFormatter : ConsoleFormatter {
    public const string FormatterName = "kinetix-json";

    public const string RequestIdField = "request_id";

    private static readonly string[] RequestIdScopeKeys = ["RequestId", "CorrelationId"];

    private const string OriginalFormatKey = "{OriginalFormat}";

    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = true,
    };

    public KinetixJsonConsoleFormatter() : base(FormatterName) { }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter
    ) {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null) {
            return;
        }

        var scopes = CollectScopes(scopeProvider);

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, WriterOptions)) {
            json.WriteStartObject();

            json.WriteString("timestamp", DateTime.UtcNow.ToString("O"));
            json.WriteString("level", logEntry.LogLevel.ToString());
            json.WriteString("logger", logEntry.Category);
            json.WriteString("message", message ?? string.Empty);

            var requestId = RequestIdOf(scopes);
            if (requestId is not null) {
                json.WriteString(RequestIdField, requestId);
            }

            if (logEntry.EventId.Id != 0) {
                json.WriteNumber("event_id", logEntry.EventId.Id);
            }

            if (logEntry.Exception is not null) {
                json.WriteString("exception", logEntry.Exception.ToString());
            }

            WriteState(json, logEntry.State);
            WriteScopes(json, scopes);

            json.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        textWriter.Write(Environment.NewLine);
    }

    private static Dictionary<string, object?> CollectScopes(IExternalScopeProvider? scopeProvider) {
        var scopes = new Dictionary<string, object?>(StringComparer.Ordinal);

        scopeProvider?.ForEachScope(static (scope, into) => {
            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs) {
                foreach (var pair in pairs) {
                    if (pair.Key != OriginalFormatKey) {
                        into[pair.Key] = pair.Value;
                    }
                }
            }
        }, scopes);

        return scopes;
    }

    private static string? RequestIdOf(Dictionary<string, object?> scopes) {
        foreach (var key in RequestIdScopeKeys) {
            if (!scopes.TryGetValue(key, out var value)) {
                continue;
            }

            var text = value?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) {
                return text;
            }
        }

        return null;
    }

    private static void WriteState<TState>(Utf8JsonWriter json, TState state) {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) {
            return;
        }

        var opened = false;
        foreach (var value in values) {
            if (value.Key == OriginalFormatKey) {
                continue;
            }

            if (!opened) {
                json.WriteStartObject("state");
                opened = true;
            }

            WriteValue(json, value.Key, value.Value);
        }

        if (opened) {
            json.WriteEndObject();
        }
    }

    private static void WriteScopes(Utf8JsonWriter json, Dictionary<string, object?> scopes) {
        if (scopes.Count == 0) {
            return;
        }

        json.WriteStartObject("scopes");
        foreach (var scope in scopes) {
            WriteValue(json, scope.Key, scope.Value);
        }
        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, string name, object? value) {
        switch (value) {
            case null:
                json.WriteNull(name);
                break;
            case string text:
                json.WriteString(name, text);
                break;
            case bool flag:
                json.WriteBoolean(name, flag);
                break;
            case int number:
                json.WriteNumber(name, number);
                break;
            case long number:
                json.WriteNumber(name, number);
                break;
            case double number:
                json.WriteNumber(name, number);
                break;
            case decimal number:
                json.WriteNumber(name, number);
                break;
            default:
                json.WriteString(name, value.ToString());
                break;
        }
    }
}
