namespace Kinetix.OrderService.Application.Ports;

public interface IMerchantStanding {
    Task<bool> MaySellAsync(string merchantPrincipalId, CancellationToken cancellationToken = default);
}
