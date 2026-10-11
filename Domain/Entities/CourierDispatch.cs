using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kinetix.OrderService.Domain.Entities;

[Table("courier_dispatches")]
public class CourierDispatch {
    [Key]
    [Column("order_number")]
    [MaxLength(64)]
    public string OrderNumber { get; set; } = string.Empty;

    [Required]
    [Column("merchant_principal_id")]
    [MaxLength(64)]
    public string MerchantPrincipalId { get; set; } = string.Empty;

    [Required]
    [Column("fulfillment_task_id")]
    [MaxLength(64)]
    public string FulfillmentTaskId { get; set; } = string.Empty;

    [Column("dispatch_ref")]
    [MaxLength(64)]
    public string? DispatchRef { get; set; }

    [Column("awb_number")]
    [MaxLength(64)]
    public string? AwbNumber { get; set; }

    [Column("dispatched_at")]
    public DateTime? DispatchedAt { get; set; }

    [Column("awb_recorded_at")]
    public DateTime? AwbRecordedAt { get; set; }

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
