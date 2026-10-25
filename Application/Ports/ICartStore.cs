using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Domain.Entities;

namespace Kinetix.OrderService.Application.Ports;

public interface ICartStore {
    Task<StoredCart> ReadAsync(string customerPrincipalId);

    Task<bool> TryReplaceAsync(
        string customerPrincipalId,
        CustomerCart cart,
        string expectedVersion,
        TimeSpan timeToLive
    );

    Task<bool> TryDeleteAsync(string customerPrincipalId, string expectedVersion);

    Task DeleteAsync(string customerPrincipalId);
}
