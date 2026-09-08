namespace Kinetix.OrderService.Domain.Entities;

public enum CompensationAttemptOutcome {
    Released,
    Partial,
    Abandoned,
    LeaseLost,
    Crashed,
}
