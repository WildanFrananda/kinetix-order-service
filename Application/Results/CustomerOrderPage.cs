using Kinetix.OrderService.DTOs.Responses;

namespace Kinetix.OrderService.Application.Results;

public record CustomerOrderPage(IReadOnlyList<OrderResponse> Orders, int TotalCount);
