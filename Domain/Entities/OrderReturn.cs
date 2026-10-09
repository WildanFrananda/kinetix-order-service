using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Domain.Entities;

[Table("order_returns")]
public class OrderReturn {
    [Key]
    [Column("return_number")]
    [MaxLength(64)]
    public string ReturnNumber { get; set; } = string.Empty;

    [Required]
    [Column("order_number")]
    [MaxLength(64)]
    public string OrderNumber { get; set; } = string.Empty;

    [Required]
    [Column("merchant_principal_id")]
    [MaxLength(64)]
    public string MerchantPrincipalId { get; set; } = string.Empty;

    [Required]
    [Column("reason")]
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [Column("status")]
    public ReturnStatus Status { get; set; } = ReturnStatus.OPEN;

    [Column("opened_at")]
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    [Column("goods_received_at")]
    public DateTime? GoodsReceivedAt { get; set; }

    [Column("bin_code")]
    [MaxLength(64)]
    public string? BinCode { get; set; }

    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }

    [Column("rejected_at")]
    public DateTime? RejectedAt { get; set; }

    [Column("rejection_reason")]
    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    [Column("refund_amount", TypeName = "decimal(18,2)")]
    public decimal? RefundAmount { get; set; }

    [Column("refund_attempts")]
    public int RefundAttempts { get; set; }

    [Column("last_refund_error")]
    [MaxLength(500)]
    public string? LastRefundError { get; set; }

    [Column("next_refund_attempt_at")]
    public DateTime? NextRefundAttemptAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<OrderReturnLine> Lines { get; set; } = [];
}
