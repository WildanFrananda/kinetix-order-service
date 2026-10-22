namespace Kinetix.OrderService.Application.Checkout;

public record ForwardPassOutcome(bool LeaseLost, string? FailureReason, bool DependencyUnavailable = false) {
    public static ForwardPassOutcome Completed() => new(false, null);

    public static ForwardPassOutcome Failed(string reason) => new(false, reason);

    public static ForwardPassOutcome Unavailable(string reason) => new(false, reason, true);

    public static ForwardPassOutcome Lost() => new(true, null);
}
