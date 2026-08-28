namespace Kinetix.OrderService.Application.Services;

public interface IPricingClient {
    Task<decimal> CalculateVoucherDiscountAsync(string voucherCode, decimal subtotal);
}
