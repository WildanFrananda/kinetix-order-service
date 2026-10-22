using Microsoft.AspNetCore.Diagnostics;

namespace Kinetix.OrderService.Infrastructure.Http;

public class UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler {
    private readonly ILogger<UnhandledExceptionHandler> _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken
    ) {
        var traceId = context.TraceIdentifier;

        _logger.LogError(
            exception,
            "unhandled exception serving {Method} {Path} (trace {TraceId})",
            context.Request.Method,
            context.Request.Path,
            traceId
        );

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await context.Response.WriteAsJsonAsync(
            new {
                error = "INTERNAL_ERROR",
                message = "something went wrong handling this request, and whether any of it took effect "
                        + "is not known here. Check before repeating it; repeat a checkout only with the same "
                        + "Idempotency-Key, which cannot charge twice.",
                traceId,
            },
            cancellationToken
        );

        return true;
    }
}
