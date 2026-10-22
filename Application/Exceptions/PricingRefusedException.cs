namespace Kinetix.OrderService.Application.Exceptions;

public class PricingRefusedException(string reason, Exception cause)
    : Exception($"pricing refused to price this order: {reason}", cause) {
    public string Reason { get; } = reason;
}
