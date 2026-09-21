using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Application.Returns;

public record ReturnGoodsReceivedOutcome(
    bool Accepted,
    bool AlreadyRecorded,
    ReturnStatus Status,
    string? Fault
);
