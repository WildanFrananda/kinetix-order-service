namespace Kinetix.OrderService.Application.Services;

public record PriceLine(string ProductId, string? CategoryId, decimal UnitPrice, int Quantity);
