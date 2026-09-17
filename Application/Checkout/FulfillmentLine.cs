namespace Kinetix.OrderService.Application.Checkout;

public record FulfillmentLine(string Sku, string ProductName, int Quantity, decimal UnitPrice);
