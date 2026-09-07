namespace Kinetix.OrderService.Application.Services;

public record PricedLine(string ProductId, int Quantity, string? AppliedFlashSaleId);
