namespace Kinetix.OrderService.Application.Results;

public record PricedLine(string ProductId, int Quantity, string? AppliedFlashSaleId);
