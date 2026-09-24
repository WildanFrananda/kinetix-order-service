namespace Kinetix.OrderService.Application.Exceptions;

public class ProductHasNoUsablePriceException(string productId, string reason)
    : Exception($"catalog states no chargeable price for '{productId}': {reason}") {
    public string ProductId { get; } = productId;
}
