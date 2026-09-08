using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kinetix.OrderService.Domain.Entities;

[Table("checkout_sagas")]
public class CheckoutSaga {
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column("order_number")]
    public string OrderNumber { get; set; } = string.Empty;

    [Required]
    [Column("customer_principal_id")]
    public string CustomerPrincipalId { get; set; } = string.Empty;

    [Required]
    [Column("state")]
    public SagaState State { get; set; } = SagaState.Running;

    [Column("failure_reason")]
    public string? FailureReason { get; set; }

    [Column("compensation_attempts")]
    public int CompensationAttempts { get; set; }

    [Column("lease_owner")]
    public string? LeaseOwner { get; set; }

    [Column("lease_expires_at")]
    public DateTime? LeaseExpiresAt { get; set; }

    [Column("next_attempt_at")]
    public DateTime? NextAttemptAt { get; set; }

    [Column("abandoned_at")]
    public DateTime? AbandonedAt { get; set; }

    [Column("needs_attention_at")]
    public DateTime? NeedsAttentionAt { get; set; }

    [Column("last_failure_code")]
    [MaxLength(40)]
    public string? LastFailureCode { get; set; }

    [Required]
    [Column("correlation_id")]
    public string CorrelationId { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<CheckoutSagaStep> Steps { get; set; } = [];
}
