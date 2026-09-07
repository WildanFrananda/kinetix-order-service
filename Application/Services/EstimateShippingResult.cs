namespace Kinetix.OrderService.Application.Services;

public record EstimateShippingResult(
    double DistanceKm,
    List<ShippingOptionResult> Options
);
