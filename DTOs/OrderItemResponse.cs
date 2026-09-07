namespace Kinetix.OrderService.DTOs;

public record OrderItemResponse(
    Guid Id,
    string ProductId,
    string ProductTitle,
    decimal UnitPrice,
    int Quantity,
    decimal LineSubtotal
);
