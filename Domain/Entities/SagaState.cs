namespace Kinetix.OrderService.Domain.Entities;

public enum SagaState {
    Running,
    Completed,
    Compensating,
    Compensated,
    Stuck,
}
