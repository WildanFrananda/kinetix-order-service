namespace Kinetix.OrderService.Application.Exceptions;

public class EmptyCartException()
    : Exception("this cart has no items, so there is nothing to check out");
