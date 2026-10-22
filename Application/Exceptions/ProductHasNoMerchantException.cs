namespace Kinetix.OrderService.Application.Exceptions;

public class ProductHasNoMerchantException(string productId)
    : Exception($"catalog names no merchant for '{productId}', so there is nobody to pay or to ship from") {
    public string ProductId { get; } = productId;
}
