namespace Kinetix.OrderService.Application.Services;


public interface IPricingClient {
    Task<PriceCalculationResult> CalculatePriceAsync(
        string? voucherCode, IReadOnlyList<PriceLine> lines, decimal baseShippingFee
    );
}
