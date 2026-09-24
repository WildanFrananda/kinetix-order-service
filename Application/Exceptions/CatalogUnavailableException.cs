namespace Kinetix.OrderService.Application.Exceptions;

public class CatalogUnavailableException(string productId, Exception cause)
    : Exception($"catalog did not answer for '{productId}', so its price cannot be established", cause) {
    public string ProductId { get; } = productId;
}
