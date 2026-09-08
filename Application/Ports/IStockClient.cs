using Kinetix.OrderService.Application.Checkout;

namespace Kinetix.OrderService.Application.Ports;

public interface IStockClient {
    Task<StepResult> ReserveStockAsync(string merchantPrincipalId, string sku, int quantity, string orderNumber);
    Task<StepResult> ReleaseStockAsync(string merchantPrincipalId, string sku, int quantity, string orderNumber);
}
