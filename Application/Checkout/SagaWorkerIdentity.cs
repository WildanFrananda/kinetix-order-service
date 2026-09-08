namespace Kinetix.OrderService.Application.Checkout;

public class SagaWorkerIdentity {
    public string Value { get; } =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public string NewLeaseOwner() => $"{Value}:{Guid.NewGuid():N}";
}
