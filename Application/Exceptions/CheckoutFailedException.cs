namespace Kinetix.OrderService.Application.Exceptions;

public class CheckoutFailedException(string orderNumber, string reason)
    : Exception($"checkout {orderNumber} was rolled back: {reason}") {
    public string OrderNumber { get; } = orderNumber;
    public string Reason { get; } = reason;
}
