using Kinetix.OrderService.Application.Lifecycle;

namespace Kinetix.OrderService.Application.Ports;

public interface IOrderLifecycleFeed {
    Task<LifecyclePage> ReadAfterAsync(
        ulong transactionId, long sequence, int limit, CancellationToken cancellationToken
    );
}
