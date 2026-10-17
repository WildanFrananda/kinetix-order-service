using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Domain.Entities;

[Table("order_lifecycle_events")]
public class OrderLifecycleEvent {
    [Key]
    [Column("sequence")]
    public long Sequence { get; set; }

    [Column("transaction_id")]
    public ulong TransactionId { get; set; }

    [Required]
    [MaxLength(50)]
    [Column("order_number")]
    public string OrderNumber { get; set; } = string.Empty;

    [Column("kind")]
    public LifecycleEventKind Kind { get; set; }

    [Column("from_status")]
    public OrderStatus? FromStatus { get; set; }

    [Column("to_status")]
    public OrderStatus? ToStatus { get; set; }

    [MaxLength(64)]
    [Column("return_number")]
    public string? ReturnNumber { get; set; }

    [Column("return_status")]
    public ReturnStatus? ReturnStatus { get; set; }

    [Column("actor_kind")]
    public LifecycleActorKind ActorKind { get; set; }

    [MaxLength(64)]
    [Column("actor_principal_id")]
    public string? ActorPrincipalId { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("buyer_principal_id")]
    public string BuyerPrincipalId { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    [Column("merchant_principal_id")]
    public string MerchantPrincipalId { get; set; } = string.Empty;

    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; }
}
