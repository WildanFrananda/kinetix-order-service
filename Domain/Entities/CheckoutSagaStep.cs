using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kinetix.OrderService.Domain.Entities;

[Table("checkout_saga_steps")]
public class CheckoutSagaStep {
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("saga_id")]
    public Guid SagaId { get; set; }

    [ForeignKey(nameof(SagaId))]
    public CheckoutSaga? Saga { get; set; }

    [Required]
    [Column("name")]
    public SagaStepName Name { get; set; }

    [Required]
    [Column("reference")]
    public string Reference { get; set; } = string.Empty;

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("merchant_principal_id")]
    public string MerchantPrincipalId { get; set; } = string.Empty;

    [Column("product_id")]
    public string? ProductId { get; set; }

    [Required]
    [Column("state")]
    public SagaStepState State { get; set; } = SagaStepState.Attempting;

    [Column("detail")]
    public string? Detail { get; set; }

    [Column("compensation_attempts")]
    public int CompensationAttempts { get; set; }

    [Column("last_failure_code")]
    [MaxLength(40)]
    public string? LastFailureCode { get; set; }

    [Column("compensated_by_repeat")]
    public bool CompensatedByRepeat { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
