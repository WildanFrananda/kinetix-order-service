namespace Kinetix.OrderService.Application.Results;

public record EstimateShippingResult(
    double DistanceKm,
    List<ShippingOptionResult> Options
);
