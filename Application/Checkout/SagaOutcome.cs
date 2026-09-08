namespace Kinetix.OrderService.Application.Services;

public record SagaOutcome(bool Succeeded, string? FailureReason);
