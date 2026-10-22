namespace Kinetix.OrderService.Domain.Entities;

public class CartItem {
    public const int MinQuantity = 1;
    public const int MaxQuantity = 999;

    public string ProductId { get; set; } = string.Empty;
    public string ProductTitle { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public string? CategoryId { get; set; }
    public string? MerchantPrincipalId { get; set; }
    public decimal LineTotal => UnitPrice * Quantity;
}
