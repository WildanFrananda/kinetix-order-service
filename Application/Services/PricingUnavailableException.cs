namespace Kinetix.OrderService.Application.Services;

public class PricingUnavailableException(Exception cause)
    : Exception("this order cannot be priced because pricing did not answer", cause);
