using Kinetix.OrderService.Application.Checkout;

namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingTierNotEstablishedException(
    string requestedTier,
    string? rateCardReason,
    IReadOnlyList<string> availableTiers
) : Exception(
    $"shipping tier '{requestedTier}' could not be established for this order: matching declined "
  + $"it against {ShippingRateCardProbe.AvailabilityBasis}"
) {
    public string RequestedTier { get; } = requestedTier;

    public string? RateCardReason { get; } = rateCardReason;

    public IReadOnlyList<string> AvailableTiers { get; } = availableTiers;
}
