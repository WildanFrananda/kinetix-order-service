using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Application.Services;

public record CustomerOrderPage(IReadOnlyList<OrderResponse> Orders, int TotalCount);
