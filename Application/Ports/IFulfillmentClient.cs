using Kinetix.OrderService.Application.Checkout;

namespace Kinetix.OrderService.Application.Ports;

public interface IFulfillmentClient {
    Task<FulfillmentCreated> CreateOrderAsync(
        string merchantPrincipalId,
        string orderNumber,
        string shippingAddress,
        string recipientName,
        string recipientPhone,
        decimal totalAmount,
        IReadOnlyList<FulfillmentLine> lines
    );

    Task<StepResult> CancelOrderAsync(string merchantPrincipalId, string warehouseOrderId);
}
