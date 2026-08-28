namespace Kinetix.OrderService.DTOs;

public record CheckoutRequest(
    string ShippingAddress,
    string? VoucherCode
);

public record UpdateOrderStatusRequest(
    string Status
);
