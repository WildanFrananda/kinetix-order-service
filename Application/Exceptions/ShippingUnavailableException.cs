namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingUnavailableException(Exception cause)
    : Exception("this order cannot be shipped because matching did not answer", cause);
