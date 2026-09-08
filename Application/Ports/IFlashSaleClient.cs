using Kinetix.OrderService.Application.Checkout;

namespace Kinetix.OrderService.Application.Ports;

/// <summary>The two halves of a flash sale's stock.</summary>
public interface IFlashSaleClient {
    Task<StepResult> AllocateAsync(string flashSaleId, string productId, int quantity, string orderNumber);
    Task<StepResult> ReleaseAsync(string flashSaleId, string productId, int quantity, string orderNumber);
}
