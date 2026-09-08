namespace Kinetix.OrderService.Application.Results;

public record ShippingOptionResult(
    string ServiceTier,
    string ServiceName,
    double DistanceKm,
    decimal BaseShippingFee,
    string EstimatedDeliveryTime,
    bool IsAvailable,
    string? UnavailableReason
);
