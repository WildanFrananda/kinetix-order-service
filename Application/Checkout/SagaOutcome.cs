namespace Kinetix.OrderService.Application.Checkout;

public record SagaOutcome(bool Succeeded, string? FailureReason);
