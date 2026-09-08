using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Application.Results;

public record SelectedShippingQuote(
    string ServiceTier,
    decimal BaseShippingFee,
    double DistanceKm,
    ShippingQuoteBasis Basis
);
