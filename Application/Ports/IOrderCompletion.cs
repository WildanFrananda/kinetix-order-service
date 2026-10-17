using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Application.Lifecycle;

namespace Kinetix.OrderService.Application.Ports;

public interface IOrderCompletion {
    Task<CompletionOutcome> CompleteAsync(
        string orderNumber, bool waitForReturnWindow, LifecycleActor actor, CancellationToken cancellationToken
    );

    Task<bool> TryReleaseAsync(EscrowRelease release, CancellationToken cancellationToken);
}
