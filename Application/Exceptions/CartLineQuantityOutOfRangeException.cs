namespace Kinetix.OrderService.Application.Exceptions;

public class CartLineQuantityOutOfRangeException(string productId, long quantity, int minimum, int maximum)
    : Exception(
        $"the cart would hold {quantity} of '{productId}', and a line holds {minimum} to {maximum}"
    ) {
    public string ProductId { get; } = productId;
    public long Quantity { get; } = quantity;
    public int Minimum { get; } = minimum;
    public int Maximum { get; } = maximum;
}
