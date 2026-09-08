using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Domain.Entities;

[Table("checkout_saga_compensation_attempts")]
public class CompensationAttempt {
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("saga_id")]
    public Guid SagaId { get; set; }

    [ForeignKey(nameof(SagaId))]
    public CheckoutSaga? Saga { get; set; }

    [Column("attempt_no")]
    public int AttemptNo { get; set; }

    [Required]
    [Column("worker")]
    public string Worker { get; set; } = string.Empty;

    [Column("started_at")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    [Column("finished_at")]
    public DateTime? FinishedAt { get; set; }

    [Column("outcome")]
    public CompensationAttemptOutcome? Outcome { get; set; }

    [Column("detail")]
    public string? Detail { get; set; }
}
