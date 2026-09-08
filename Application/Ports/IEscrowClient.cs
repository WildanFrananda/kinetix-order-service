using Kinetix.OrderService.Application.Checkout;

namespace Kinetix.OrderService.Application.Ports;

public interface IEscrowClient {
    Task<StepResult> CreateHoldAsync(
        string orderNumber,
        string customerPrincipalId,
        string merchantPrincipalId,
        string? driverPrincipalId,
        decimal totalOrderAmount,
        decimal merchantAmount,
        decimal shippingFeeAmount
    );

    Task<StepResult> RefundHoldAsync(string orderNumber, string reason);

    Task<EscrowStanding> GetStandingAsync(string orderNumber);
}
