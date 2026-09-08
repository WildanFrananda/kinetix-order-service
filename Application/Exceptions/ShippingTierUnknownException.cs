namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingTierUnknownException(string requestedTier, IReadOnlyList<string> availableTiers)
    : Exception($"matching lists no service tier named '{requestedTier}'") {
    public string RequestedTier { get; } = requestedTier;
    public IReadOnlyList<string> AvailableTiers { get; } = availableTiers;
}
