namespace Kinetix.OrderService.DTOs;

public record AddCartItemRequest(
    string ProductId,
    string ProductTitle,
    decimal UnitPrice,
    int Quantity,
    string? CategoryId
);

public record UpdateCartItemRequest(
    int Quantity
);

public record ApplyCartVoucherRequest(
    string VoucherCode
);
