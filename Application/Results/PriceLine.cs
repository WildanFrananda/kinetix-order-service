namespace Kinetix.OrderService.Application.Results;

public record PriceLine(string ProductId, string? CategoryId, decimal UnitPrice, int Quantity);
