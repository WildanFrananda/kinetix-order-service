using System.Text.Json;
using Kinetix.OrderService.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class KinetixJsonConsoleFormatterTests {
    [Fact]
    public void OneEventIsOneLineOfOneJsonObject() {
        var line = Format("checkout finished", scopes: null);

        Assert.DoesNotContain('\n', line.TrimEnd('\r', '\n'));

        var log = JsonDocument.Parse(line).RootElement;

        Assert.Equal("checkout finished", log.GetProperty("message").GetString());
        Assert.Equal("Information", log.GetProperty("level").GetString());
        Assert.Equal("Kinetix.OrderService.Tests.Source", log.GetProperty("logger").GetString());
        Assert.True(log.TryGetProperty("timestamp", out _));
    }

    [Theory]
    [InlineData("kinetix-trace-1757000000-4242")]
    [InlineData("kinetix+trace/1757000000=4242")]
    [InlineData("kinetix-trace-<4242>&more")]
    [InlineData("0HNDAJK4LQ7DP:00000001")]
    public void APlainGrepForTheRawIdStillMatchesTheLine(string requestId) {
        var line = Format("gRPC ServerReflectionInfo from review", new() { ["RequestId"] = requestId });

        Assert.Contains(requestId, line, StringComparison.Ordinal);
        Assert.Equal(
            requestId,
            JsonDocument.Parse(line).RootElement.GetProperty("request_id").GetString()
        );
    }

    [Fact]
    public void WorkWithNoRequestBehindItFallsBackToTheSagasOwnCorrelationId() {
        var line = Format("compensating", new() { ["CorrelationId"] = "kinetix-trace-9" });

        Assert.Equal(
            "kinetix-trace-9",
            JsonDocument.Parse(line).RootElement.GetProperty("request_id").GetString()
        );
    }

    [Fact]
    public void ARequestIdBeatsTheSagaIdWhenBothAreInScope() {
        var line = Format("compensating inside a request", new() {
            ["CorrelationId"] = "kinetix-trace-older",
            ["RequestId"] = "kinetix-trace-now",
        });

        var log = JsonDocument.Parse(line).RootElement;

        Assert.Equal("kinetix-trace-now", log.GetProperty("request_id").GetString());
        Assert.Equal(
            "kinetix-trace-older",
            log.GetProperty("scopes").GetProperty("CorrelationId").GetString()
        );
    }

    [Fact]
    public void ALineWithNoCorrelationIdCarriesNoRequestIdField() {
        var log = JsonDocument.Parse(Format("starting up", scopes: null)).RootElement;

        Assert.False(log.TryGetProperty("request_id", out _));
    }

    [Fact]
    public void TheStructuredContextTheServiceAlreadyAttachesIsKept() {
        var writer = new StringWriter();
        var scopeProvider = new LoggerExternalScopeProvider();
        using var scope = scopeProvider.Push(new Dictionary<string, object> {
            ["SagaId"] = "saga-1",
            ["Attempt"] = 3,
        });

        var state = new List<KeyValuePair<string, object?>> {
            new("Order", "KTX-1"),
            new("{OriginalFormat}", "unwinding {Order}"),
        };

        new KinetixJsonConsoleFormatter().Write(
            new LogEntry<List<KeyValuePair<string, object?>>>(
                LogLevel.Warning,
                "Kinetix.OrderService.Tests.Source",
                new EventId(0),
                state,
                null,
                static (_, _) => "unwinding KTX-1"
            ),
            scopeProvider,
            writer
        );

        var log = JsonDocument.Parse(writer.ToString()).RootElement;

        Assert.Equal("KTX-1", log.GetProperty("state").GetProperty("Order").GetString());
        Assert.Equal("saga-1", log.GetProperty("scopes").GetProperty("SagaId").GetString());
        Assert.Equal(3, log.GetProperty("scopes").GetProperty("Attempt").GetInt32());
        Assert.False(log.GetProperty("state").TryGetProperty("{OriginalFormat}", out _));
    }

    [Fact]
    public void AnExceptionTravelsOnTheSameLineAsTheMessage() {
        var writer = new StringWriter();

        new KinetixJsonConsoleFormatter().Write(
            new LogEntry<string>(
                LogLevel.Error,
                "Kinetix.OrderService.Tests.Source",
                new EventId(0),
                "state",
                new InvalidOperationException("pricing did not answer"),
                static (_, _) => "checkout refused"
            ),
            null,
            writer
        );

        var line = writer.ToString();

        Assert.DoesNotContain('\n', line.TrimEnd('\r', '\n'));
        Assert.Contains(
            "pricing did not answer",
            JsonDocument.Parse(line).RootElement.GetProperty("exception").GetString()
        );
    }

    private static string Format(string message, Dictionary<string, object>? scopes) {
        var writer = new StringWriter();
        var scopeProvider = new LoggerExternalScopeProvider();
        var scope = scopes is null ? null : scopeProvider.Push(scopes);

        try {
            new KinetixJsonConsoleFormatter().Write(
                new LogEntry<string>(
                    LogLevel.Information,
                    "Kinetix.OrderService.Tests.Source",
                    new EventId(0),
                    "state",
                    null,
                    (_, _) => message
                ),
                scopeProvider,
                writer
            );
        } finally {
            scope?.Dispose();
        }

        return writer.ToString();
    }
}
