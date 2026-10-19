namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingTierNotEstablishedException(
    string requestedTier,
    string? rateCardReason,
    IReadOnlyList<string> availableTiers
) : Exception(
    $"shipping tier '{requestedTier}' could not be established for this parcel on this route: "
  + (rateCardReason ?? "no reason was given")
) {
    public string RequestedTier { get; } = requestedTier;

    public string? RateCardReason { get; } = rateCardReason;

    public IReadOnlyList<string> AvailableTiers { get; } = availableTiers;
}
