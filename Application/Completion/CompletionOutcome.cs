namespace Kinetix.OrderService.Application.Completion;

public record CompletionOutcome(CompletionStatus Status, bool EscrowReleased, string? Detail);
