namespace Kinetix.OrderService.DTOs.Requests;

public record AddCartItemRequest(
    string ProductId,
    int Quantity
);
