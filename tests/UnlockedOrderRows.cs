using Kinetix.OrderService.Application.Ports;

namespace Kinetix.OrderService.Tests;

public sealed class UnlockedOrderRows : IOrderRowLock {
    public int Locks { get; private set; }

    public Task LockAsync(string orderNumber, CancellationToken cancellationToken) {
        Locks += 1;
        return Task.CompletedTask;
    }
}
