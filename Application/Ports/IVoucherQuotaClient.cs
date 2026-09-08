namespace Kinetix.OrderService.Application.Services;

public interface IVoucherQuotaClient {
    Task<StepResult> RedeemVoucherAsync(string voucherCode, string orderNumber, string customerPrincipalId);
    Task<StepResult> ReleaseVoucherAsync(string voucherCode, string orderNumber);
}
