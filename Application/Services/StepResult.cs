namespace Kinetix.OrderService.Application.Services;

public record StepResult(bool Success, bool AlreadyDone, string? Detail) {
    public static StepResult Ok() => new(true, false, null);
    public static StepResult Repeat() => new(true, true, null);
    public static StepResult Refused(string detail) => new(false, false, detail);
}
