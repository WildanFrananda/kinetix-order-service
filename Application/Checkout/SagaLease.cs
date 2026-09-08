namespace Kinetix.OrderService.Application.Checkout;

public record SagaLease(Guid SagaId, string Owner, int AttemptNumber);
