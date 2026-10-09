using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kinetix.OrderService.Domain.Entities;

[Table("escrow_releases")]
public class EscrowRelease {
    [Key]
    [Column("order_number")]
    [MaxLength(64)]
    public string OrderNumber { get; set; } = string.Empty;

    [Column("released_at")]
    public DateTime? ReleasedAt { get; set; }

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
