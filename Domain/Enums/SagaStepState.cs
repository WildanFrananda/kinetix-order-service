namespace Kinetix.OrderService.Domain.Enums;

public enum SagaStepState {
    Attempting,
    Done,
    Failed,
    Compensated,
}
