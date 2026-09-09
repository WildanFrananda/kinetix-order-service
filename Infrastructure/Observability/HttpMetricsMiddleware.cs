using System.Diagnostics;
using Grpc.AspNetCore.Server;

namespace Kinetix.OrderService.Infrastructure.Observability;

public class HttpMetricsMiddleware(RequestDelegate next, KinetixMetrics metrics) {
    private readonly RequestDelegate _next = next;
    private readonly KinetixMetrics _metrics = metrics;

    public async Task InvokeAsync(HttpContext context) {
        var started = Stopwatch.GetTimestamp();

        try {
            await _next(context);
        } finally {
            if (context.GetEndpoint()?.Metadata.GetMetadata<GrpcMethodMetadata>() is null) {
                _metrics.ObserveHttp(
                    HttpMethodLabel.Of(context.Request.Method),
                    RouteLabel.Of(context),
                    context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(started).TotalSeconds
                );
            }
        }
    }
}
