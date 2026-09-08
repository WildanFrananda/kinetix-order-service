namespace Kinetix.OrderService.Application.Services;

public class SagaWorkerIdentity {
    public string Value { get; } =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public string NewLeaseOwner() => $"{Value}:{Guid.NewGuid():N}";
}
