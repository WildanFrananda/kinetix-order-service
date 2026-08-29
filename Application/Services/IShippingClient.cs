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

public record EstimateShippingResult(
    double DistanceKm,
    List<ShippingOptionResult> Options
);

public interface IShippingClient {
    Task<EstimateShippingResult> EstimateShippingOptionsAsync(double originLat, double originLng, double destLat, double destLng, double totalWeightKg, long? merchantId = null);
}
