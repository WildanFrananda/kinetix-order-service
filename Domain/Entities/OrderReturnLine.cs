using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kinetix.OrderService.Domain.Entities;

[Table("order_return_lines")]
public class OrderReturnLine {
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("return_number")]
    [MaxLength(64)]
    public string ReturnNumber { get; set; } = string.Empty;

    [Required]
    [Column("sku")]
    [MaxLength(100)]
    public string Sku { get; set; } = string.Empty;

    [Column("quantity")]
    public int Quantity { get; set; }

    public OrderReturn? Return { get; set; }
}
