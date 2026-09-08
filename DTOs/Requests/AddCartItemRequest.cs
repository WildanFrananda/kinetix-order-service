namespace Kinetix.OrderService.DTOs.Requests;

public record AddCartItemRequest(
    string ProductId,
    string ProductTitle,
    decimal UnitPrice,
    int Quantity,
    string? CategoryId,
    string? MerchantPrincipalId
);
