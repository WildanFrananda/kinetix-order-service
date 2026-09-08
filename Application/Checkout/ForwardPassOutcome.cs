namespace Kinetix.OrderService.Application.Checkout;

public record ForwardPassOutcome(bool LeaseLost, string? FailureReason) {
    public static ForwardPassOutcome Completed() => new(false, null);

    public static ForwardPassOutcome Failed(string reason) => new(false, reason);

    public static ForwardPassOutcome Lost() => new(true, null);
}
