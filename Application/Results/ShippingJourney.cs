namespace Kinetix.OrderService.Application.Results;

public record ShippingJourney(
    string ServiceTier,
    double DistanceKm,
    long TotalWeightGrams
);
