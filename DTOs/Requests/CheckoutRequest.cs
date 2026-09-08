namespace Kinetix.OrderService.DTOs.Requests;

public record CheckoutRequest(
    string ShippingAddress,
    string? VoucherCode,
    string? ShippingServiceTier = null
);
