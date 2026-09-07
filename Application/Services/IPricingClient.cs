namespace Kinetix.OrderService.Application.Services;


public interface IPricingClient {
    Task<PriceCalculationResult> CalculatePriceAsync(string? voucherCode, decimal subtotal, decimal baseShippingFee);
}
