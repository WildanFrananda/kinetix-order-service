namespace Kinetix.OrderService.Application.Services;

public record SagaLease(Guid SagaId, string Owner, int AttemptNumber);
