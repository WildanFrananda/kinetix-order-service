namespace Kinetix.OrderService.Infrastructure.Http;

public class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger) {
    public const string HeaderName = "X-Request-Id";

    private readonly RequestDelegate _next = next;
    private readonly ILogger<RequestIdMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context) {
        var requestId = context.Request.Headers[HeaderName].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(requestId)) {
            context.TraceIdentifier = requestId;
        } else {
            requestId = context.TraceIdentifier;
        }

        context.Response.Headers[HeaderName] = requestId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId })) {
            await _next(context);
        }
    }
}
