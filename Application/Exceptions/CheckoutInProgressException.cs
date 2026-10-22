namespace Kinetix.OrderService.Application.Exceptions;

public class CheckoutInProgressException(string orderNumber)
    : Exception($"the checkout this Idempotency-Key started, {orderNumber}, has not finished yet") {
    public string OrderNumber { get; } = orderNumber;
}
