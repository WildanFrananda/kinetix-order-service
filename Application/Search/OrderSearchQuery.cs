using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Application.Search;

public sealed record OrderSearchQuery(
    IReadOnlySet<OrderStatus> Statuses,
    DateTime? CreatedFrom,
    DateTime? CreatedTo,
    string? MerchantPrincipalId,
    string? CustomerPrincipalId,
    string? OrderNumber,
    OrderSearchCursor? Before,
    int Limit
) {
    public const int DefaultLimit = 50;
    public const int MinLimit = 1;
    public const int MaxLimit = 100;
}
