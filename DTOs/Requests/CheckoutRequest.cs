namespace Kinetix.OrderService.DTOs.Requests;

public record CheckoutRequest(
    string ShippingAddress,
    string? VoucherCode,
    string? ShippingServiceTier = "KINETIX_REGULAR",
    decimal BaseShippingFee = 0,
    double DistanceKm = 0
);
