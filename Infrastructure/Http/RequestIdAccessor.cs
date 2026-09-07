namespace Kinetix.OrderService.Infrastructure.Http;

public class RequestIdAccessor(IHttpContextAccessor httpContextAccessor) {

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public string Current => _httpContextAccessor.HttpContext?.TraceIdentifier ?? string.Empty;
}
