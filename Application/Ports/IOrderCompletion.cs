using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Domain.Entities;

namespace Kinetix.OrderService.Application.Ports;

public interface IOrderCompletion {
    Task<CompletionOutcome> CompleteAsync(
        string orderNumber, bool waitForReturnWindow, CancellationToken cancellationToken
    );

    Task<bool> TryReleaseAsync(EscrowRelease release, CancellationToken cancellationToken);
}
