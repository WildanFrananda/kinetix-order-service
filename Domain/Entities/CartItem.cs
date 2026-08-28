namespace Kinetix.OrderService.Domain.Entities;

public class CartItem {
    public string ProductId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public string? CategoryId { get; set; }
    public decimal LineTotal => UnitPrice * Quantity;
}
