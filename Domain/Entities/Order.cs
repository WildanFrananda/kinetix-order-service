using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Domain.Entities;

[Table("orders")]
public class Order {
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(50)]
    [Column("order_number")]
    public string OrderNumber { get; set; } = string.Empty;

    [Required]
    [Column("customer_id")]
    public long CustomerId { get; set; }

    [Required]
    [Column("status")]
    public OrderStatus Status { get; set; } = OrderStatus.PENDING_PAYMENT;

    [Required]
    [Column("subtotal", TypeName = "decimal(18,2)")]
    public decimal Subtotal { get; set; }

    [Required]
    [Column("discount_amount", TypeName = "decimal(18,2)")]
    public decimal DiscountAmount { get; set; }

    [MaxLength(50)]
    [Column("applied_voucher")]
    public string? AppliedVoucher { get; set; }

    [Required]
    [Column("final_total", TypeName = "decimal(18,2)")]
    public decimal FinalTotal { get; set; }

    [Required]
    [MaxLength(500)]
    [Column("shipping_address")]
    public string ShippingAddress { get; set; } = string.Empty;

    [MaxLength(100)]
    [Column("idempotency_key")]
    public string? IdempotencyKey { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<OrderItem> Items { get; set; } = [];
}
