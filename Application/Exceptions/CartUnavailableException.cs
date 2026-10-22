namespace Kinetix.OrderService.Application.Exceptions;

public class CartUnavailableException(Exception cause)
    : Exception("the cart store did not answer, so the cart could not be read or changed", cause);
