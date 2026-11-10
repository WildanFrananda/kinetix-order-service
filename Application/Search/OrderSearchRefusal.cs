namespace Kinetix.OrderService.Application.Search;

public sealed record OrderSearchRefusal(
    string Code,
    string Parameter,
    string Message,
    IReadOnlyList<string>? Allowed = null
);
