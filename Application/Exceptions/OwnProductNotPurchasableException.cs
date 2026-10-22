namespace Kinetix.OrderService.Application.Exceptions;

public class OwnProductNotPurchasableException(string productId)
    : Exception($"'{productId}' is sold by the account buying it, and a seller cannot buy its own product") {
    public string ProductId { get; } = productId;
}
