using Kinetix.OrderService.Application.Ports;

namespace Kinetix.OrderService.Tests;

public sealed class HeldDispatchClaims : ICourierDispatchClaims {
    public int Asked { get; private set; }

    public Task<bool> TryClaimAsync(string orderNumber, DateTime now, CancellationToken cancellationToken) {
        Asked += 1;
        return Task.FromResult(false);
    }
}
