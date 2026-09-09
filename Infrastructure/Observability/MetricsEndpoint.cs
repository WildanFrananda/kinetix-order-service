using Prometheus;

namespace Kinetix.OrderService.Infrastructure.Observability;

public static class MetricsEndpoint {
    private const string ExpositionContentType = "text/plain; version=0.0.4; charset=utf-8";

    private const string FailedBody =
        "# metrics could not be collected on this scrape. This is not a reading of zero.\n";

    public static IEndpointConventionBuilder Map(
        IEndpointRouteBuilder endpoints, CollectorRegistry registry
    ) {
        return Map(endpoints, registry.CollectAndExportAsTextAsync);
    }

    public static IEndpointConventionBuilder Map(
        IEndpointRouteBuilder endpoints, Func<Stream, CancellationToken, Task> collect
    ) {
        async Task serve(HttpContext context) {
            using var exposition = new MemoryStream();

            try {
                await collect(exposition, context.RequestAborted);
            } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
                return;
            } catch (Exception e) {
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(MetricsEndpoint).FullName!);

                logger.LogError(
                    e,
                    "a scrape of {Path} could not be collected; answering 503 so the gap is not "
                        + "read as zero traffic",
                    KinetixMetrics.MetricsPath
                );

                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(FailedBody, context.RequestAborted);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = ExpositionContentType;
            context.Response.ContentLength = exposition.Length;

            exposition.Position = 0;
            await exposition.CopyToAsync(context.Response.Body, context.RequestAborted);
        }

        return endpoints.MapGet(KinetixMetrics.MetricsPath, serve);
    }
}
