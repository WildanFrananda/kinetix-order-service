namespace Kinetix.OrderService.Domain.Enums;

public enum SagaState {
    Running,
    Completed,
    Compensating,
    Compensated,
    Stuck,
    Abandoned,
}
