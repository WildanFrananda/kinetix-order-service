using Kinetix.OrderService.Application.Checkout;

namespace Kinetix.OrderService.Application.Ports;

public interface IVoucherQuotaClient {
    Task<StepResult> RedeemVoucherAsync(
        string voucherCode,
        string orderNumber,
        string customerPrincipalId,
        string merchantPrincipalId
    );
    Task<StepResult> ReleaseVoucherAsync(string voucherCode, string orderNumber);
}
