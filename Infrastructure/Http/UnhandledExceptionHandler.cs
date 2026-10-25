using Kinetix.OrderService.Infrastructure.Persistence;
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

        if (exception is BadHttpRequestException unreadable) {
            _logger.LogWarning(
                exception,
                "the request for {Method} {Path} could not be read and was refused with {Status} (trace {TraceId})",
                context.Request.Method,
                context.Request.Path,
                unreadable.StatusCode,
                traceId
            );

            await Answer(context, unreadable.StatusCode, Unreadable(unreadable.StatusCode, traceId), cancellationToken);
            return true;
        }

        if (DatabaseOutage.Explains(exception)) {
            _logger.LogError(
                exception,
                "{Method} {Path} could not reach the database (trace {TraceId})",
                context.Request.Method,
                context.Request.Path,
                traceId
            );

            await Answer(
                context,
                StatusCodes.Status503ServiceUnavailable,
                new {
                    error = "DATABASE_UNAVAILABLE",
                    message = "order could not reach its database, so whether any of this request took effect "
                            + "is not known here. Try again shortly; repeat a checkout only with the same "
                            + "Idempotency-Key, which cannot charge twice.",
                    traceId,
                },
                cancellationToken
            );
            return true;
        }

        _logger.LogError(
            exception,
            "unhandled exception serving {Method} {Path} (trace {TraceId})",
            context.Request.Method,
            context.Request.Path,
            traceId
        );

        await Answer(
            context,
            StatusCodes.Status500InternalServerError,
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

    private static object Unreadable(int status, string traceId) => status switch {
        StatusCodes.Status413PayloadTooLarge => new {
            error = "REQUEST_BODY_TOO_LARGE",
            message = "the request body is larger than this service accepts, so nothing was done",
            traceId,
        },
        StatusCodes.Status408RequestTimeout => new {
            error = "REQUEST_TIMEOUT",
            message = "the request body did not arrive in time, so nothing was done",
            traceId,
        },
        _ => new {
            error = "REQUEST_UNREADABLE",
            message = "the request could not be read as sent, so nothing was done",
            traceId,
        },
    };

    private static async Task Answer(
        HttpContext context,
        int status,
        object body,
        CancellationToken cancellationToken
    ) {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(body, cancellationToken);
    }
}
