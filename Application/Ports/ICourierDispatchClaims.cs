namespace Kinetix.OrderService.Application.Ports;

public interface ICourierDispatchClaims {
    Task<bool> TryClaimAsync(string orderNumber, DateTime now, CancellationToken cancellationToken);
}
