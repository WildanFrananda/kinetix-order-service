namespace Kinetix.OrderService.Application.Services;

public record ShippingOptionResult(
    string ServiceTier,
    string ServiceName,
    double DistanceKm,
    decimal BaseShippingFee,
    string EstimatedDeliveryTime,
    bool IsAvailable,
    string? UnavailableReason
);
