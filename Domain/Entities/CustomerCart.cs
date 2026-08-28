namespace Kinetix.OrderService.Domain.Entities;

public class CustomerCart {
    public long CustomerId { get; set; }
    public List<CartItem> Items { get; set; } = [];
    public string? AppliedVoucherCode { get; set; }
    public decimal Subtotal => Items.Sum(i => i.LineTotal);
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public CustomerCart() { }

    public CustomerCart(long customerId) {
        CustomerId = customerId;
    }
}
