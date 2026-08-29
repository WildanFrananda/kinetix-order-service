namespace Kinetix.OrderService.DTOs;

public record CheckoutRequest(
    string ShippingAddress,
    string? VoucherCode,
    string? ShippingServiceTier = "KINETIX_REGULAR",
    decimal BaseShippingFee = 0,
    double DistanceKm = 0
);

public record UpdateOrderStatusRequest(
    string Status
);
