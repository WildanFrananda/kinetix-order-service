namespace Kinetix.OrderService.Application.Exceptions;

public class ProductNotInCatalogException(string productId)
    : Exception($"catalog has no product '{productId}', so there is no price to charge for it") {
    public string ProductId { get; } = productId;
}
