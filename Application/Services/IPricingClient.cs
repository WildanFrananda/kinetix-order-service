namespace Kinetix.OrderService.Application.Services;

public record PriceCalculationResult(
    decimal Subtotal,
    decimal VoucherDiscount,
    decimal BaseShippingFee,
    decimal ShippingDiscount,
    decimal FinalShippingFee,
    decimal FinalTotal
);

public interface IPricingClient {
    Task<PriceCalculationResult> CalculatePriceAsync(string? voucherCode, decimal subtotal, decimal baseShippingFee);
}
