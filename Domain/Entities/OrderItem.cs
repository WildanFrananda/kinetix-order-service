using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kinetix.OrderService.Domain.Entities;

[Table("order_items")]
public class OrderItem {
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column("order_id")]
    public Guid OrderId { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("product_title")]
    public string ProductTitle { get; set; } = string.Empty;

    [Required]
    [Column("unit_price", TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }

    [Required]
    [Column("quantity")]
    public int Quantity { get; set; }

    [Required]
    [Column("line_subtotal", TypeName = "decimal(18,2)")]
    public decimal LineSubtotal { get; set; }

    [ForeignKey("OrderId")]
    public Order? Order { get; set; }
}
