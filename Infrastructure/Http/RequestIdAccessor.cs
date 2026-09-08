namespace Kinetix.OrderService.Infrastructure.Http;

public class RequestIdAccessor(IHttpContextAccessor httpContextAccessor) {
    private static readonly AsyncLocal<string?> Ambient = new();

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public string Current =>
        Ambient.Value ?? _httpContextAccessor.HttpContext?.TraceIdentifier ?? string.Empty;

    public RequestIdScope Override(string requestId) {
        var previous = Ambient.Value;
        Ambient.Value = requestId;
        return new RequestIdScope(previous);
    }

    internal static void Restore(string? requestId) => Ambient.Value = requestId;
}
