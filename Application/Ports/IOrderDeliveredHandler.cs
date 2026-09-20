using Kinetix.OrderService.Application.Delivery;

namespace Kinetix.OrderService.Application.Ports;

public interface IOrderDeliveredHandler {
    Task<OrderDeliveredOutcome> HandleAsync(
        string orderNumber,
        string driverPrincipalId,
        DateTime deliveredAt
    );
}
