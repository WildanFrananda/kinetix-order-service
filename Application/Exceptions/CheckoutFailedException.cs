namespace Kinetix.OrderService.Application.Exceptions;

public class CheckoutFailedException(
    string orderNumber,
    string reason,
    bool rolledBack,
    bool dependencyUnavailable,
    bool replayed
) : Exception($"checkout {orderNumber} was not placed: {reason}") {
    public string OrderNumber { get; } = orderNumber;
    public string Reason { get; } = reason;
    public bool RolledBack { get; } = rolledBack;
    public bool DependencyUnavailable { get; } = dependencyUnavailable;
    public bool Replayed { get; } = replayed;
}
