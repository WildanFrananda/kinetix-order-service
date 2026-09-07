namespace Kinetix.OrderService.Domain.Entities;

public enum SagaStepState {
    Attempting,
    Done,
    Failed,
    Compensated,
}
