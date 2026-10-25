using Kinetix.OrderService.DTOs.Responses;

namespace Kinetix.OrderService.Application.Results;

public record CustomerOrderPage(IReadOnlyList<OrderResponse> Orders, int TotalCount) {
    public const int MaxPage = 10_000;
    public const int MaxPageSize = 100;
}
