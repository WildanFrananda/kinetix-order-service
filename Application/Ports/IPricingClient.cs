using Kinetix.OrderService.Application.Results;

namespace Kinetix.OrderService.Application.Ports;


public interface IPricingClient {
    Task<PriceCalculationResult> CalculatePriceAsync(
        string? voucherCode, IReadOnlyList<PriceLine> lines, decimal baseShippingFee
    );
}
