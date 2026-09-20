using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kinetix.OrderService.Domain.Entities;

[Table("shipping_settlements")]
public class ShippingSettlement {
    [Key]
    [Column("order_number")]
    [MaxLength(64)]
    public string OrderNumber { get; set; } = string.Empty;

    [Required]
    [Column("driver_principal_id")]
    [MaxLength(64)]
    public string DriverPrincipalId { get; set; } = string.Empty;

    [Column("delivered_at")]
    public DateTime DeliveredAt { get; set; }

    [Column("settled_at")]
    public DateTime? SettledAt { get; set; }

    [Column("attempts")]
    public int Attempts { get; set; }

    [Column("last_error")]
    [MaxLength(500)]
    public string? LastError { get; set; }

    [Column("next_attempt_at")]
    public DateTime? NextAttemptAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
