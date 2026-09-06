using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Application.Services;

public interface IOrderService {
    Task<OrderResponse> CheckoutAsync(string customerPrincipalId, CheckoutRequest request, string? idempotencyKey);
    Task<OrderResponse?> GetOrderByIdAsync(Guid orderId);
    Task<List<OrderResponse>> GetCustomerOrdersAsync(string customerPrincipalId, OrderStatus? status, int page, int pageSize);
    Task<OrderResponse> TransitionOrderStatusAsync(Guid orderId, OrderStatus newStatus);
}
