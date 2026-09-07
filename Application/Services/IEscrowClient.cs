namespace Kinetix.OrderService.Application.Services;

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
}
