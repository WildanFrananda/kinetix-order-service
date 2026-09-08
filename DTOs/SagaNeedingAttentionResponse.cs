namespace Kinetix.OrderService.DTOs;

public record SagaNeedingAttentionResponse(
    Guid SagaId,
    string OrderNumber,
    string CustomerPrincipalId,
    string State,
    string? OrderStatus,
    int CompensationAttempts,
    string? LastFailureCode,
    string? FailureReason,
    string CorrelationId,
    DateTime? AbandonedAt,
    DateTime? NeedsAttentionAt,
    DateTime UpdatedAt,
    List<UnreleasedStepResponse> UnreleasedSteps
);
