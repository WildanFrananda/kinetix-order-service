namespace Kinetix.OrderService.Application.Exceptions;

public class OrderTotalNotChargeableException()
    : Exception("this order comes to nothing, and payment cannot hold or charge an amount of zero");
