namespace Kinetix.OrderService.Application.Results;

public record OrderChangePage(IReadOnlyList<OrderChange> Changes, bool HasMore);
