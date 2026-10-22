namespace Kinetix.OrderService.Application.Exceptions;

public class QuantityOutOfRangeException(string productId, int quantity, int minimum, int maximum)
    : Exception($"a quantity of {quantity} for '{productId}' is outside {minimum} to {maximum}") {
    public string ProductId { get; } = productId;
    public int Quantity { get; } = quantity;
    public int Minimum { get; } = minimum;
    public int Maximum { get; } = maximum;
}
