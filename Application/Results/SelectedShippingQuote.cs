namespace Kinetix.OrderService.Application.Results;

public record SelectedShippingQuote(
    string ServiceTier,
    decimal BaseShippingFee,
    double DistanceKm
);
