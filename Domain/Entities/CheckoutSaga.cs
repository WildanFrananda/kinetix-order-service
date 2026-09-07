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

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<CheckoutSagaStep> Steps { get; set; } = [];
}
