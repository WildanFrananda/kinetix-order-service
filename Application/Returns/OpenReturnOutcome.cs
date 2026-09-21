using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Application.Returns;

public record OpenReturnOutcome(
    bool Success,
    string ReturnNumber,
    ReturnStatus Status,
    bool AlreadyOpen,
    string? Fault
);
