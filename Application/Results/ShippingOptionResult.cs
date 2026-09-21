namespace Kinetix.OrderService.Application.Results;

public record ShippingOptionResult(
    string ServiceTier,
    string ServiceName,
    double DistanceKm,
    string EstimatedDeliveryTime,
    bool IsAvailable,
    string? UnavailableReason
);
