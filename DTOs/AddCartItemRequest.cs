namespace Kinetix.OrderService.DTOs;

public record AddCartItemRequest(
    string ProductId,
    string ProductTitle,
    decimal UnitPrice,
    int Quantity,
    string? CategoryId
);
