namespace Kinetix.OrderService.Infrastructure.Observability;

public static class HttpMethodLabel {
    public const string Other = "OTHER";

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) {
        "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE", "CONNECT",
    };

    public static string Of(string? method) {
        if (string.IsNullOrEmpty(method) || !Known.Contains(method)) {
            return Other;
        }

        return method.ToUpperInvariant();
    }
}
