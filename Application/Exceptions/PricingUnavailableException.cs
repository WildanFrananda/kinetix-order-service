namespace Kinetix.OrderService.Application.Exceptions;

public class PricingUnavailableException(Exception cause)
    : Exception("this order cannot be priced because pricing did not answer", cause);
