using Kinetix.OrderService.Domain.Entities;

namespace Kinetix.OrderService.Application.Services;

public record StepCompensationOutcome(
    bool Landed,
    bool AlreadyDone,
    CompensationFailureKind Kind,
    string? Code,
    string? Detail,
    bool NeedsAttention
) {
    public static StepCompensationOutcome Released(bool alreadyDone) =>
        new(true, alreadyDone, CompensationFailureKind.Transient, null, null, false);

    public static StepCompensationOutcome ReleasedWithConcern(string code, string detail) =>
        new(true, true, CompensationFailureKind.Transient, code, detail, true);

    public static StepCompensationOutcome Retryable(string code, string? detail) =>
        new(false, false, CompensationFailureKind.Transient, code, detail, false);

    public static StepCompensationOutcome Stalled(string code, string detail) =>
        new(false, false, CompensationFailureKind.Transient, code, detail, true);

    public static StepCompensationOutcome Terminal(string code, string detail) =>
        new(false, false, CompensationFailureKind.Terminal, code, detail, true);
}
