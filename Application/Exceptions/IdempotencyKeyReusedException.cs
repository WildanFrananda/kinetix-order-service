namespace Kinetix.OrderService.Application.Exceptions;

public class IdempotencyKeyReusedException(string orderNumber)
    : Exception($"this Idempotency-Key already belongs to checkout {orderNumber}, which asked for something else") {
    public string OrderNumber { get; } = orderNumber;
}
