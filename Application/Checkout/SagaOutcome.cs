namespace Kinetix.OrderService.Application.Checkout;

public record SagaOutcome(
    bool Succeeded,
    string? FailureReason,
    bool RolledBack = false,
    bool DependencyUnavailable = false
);
