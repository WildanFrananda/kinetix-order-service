namespace Kinetix.OrderService.Application.Exceptions;

public class CartBusyException(int attempts)
    : Exception($"the cart changed under each of {attempts} attempts to edit it, so this edit was not applied") {
    public int Attempts { get; } = attempts;
}
