using Common.V1;
using Pricing.V1;

namespace Kinetix.OrderService.Application.Services;

public class PricingGrpcClient(
    PricingService.PricingServiceClient client,
    ILogger<PricingGrpcClient> logger) : IPricingClient {
    private readonly PricingService.PricingServiceClient _client = client;
    private readonly ILogger<PricingGrpcClient> _logger = logger;

    private const long MinorPerMajor = 100L;

    public async Task<PriceCalculationResult> CalculatePriceAsync(string? voucherCode, decimal subtotal, decimal baseShippingFee) {
        if (subtotal <= 0m && baseShippingFee <= 0m) {
            return new PriceCalculationResult(0m, 0m, 0m, 0m, 0m, 0m);
        }

        try {
            var request = new CalculatePriceRequest {
                VoucherCode = voucherCode ?? string.Empty,
                BaseShippingFee = ToMoney(baseShippingFee)
            };

            request.Items.Add(new PriceItemRequest {
                ProductId = "CART-ITEM",
                BasePrice = ToMoney(subtotal),
                Quantity = 1
            });

            var response = await _client.CalculatePriceAsync(request);

            return new PriceCalculationResult(
                FromMoney(response.Subtotal),
                FromMoney(response.VoucherDiscount),
                FromMoney(response.BaseShippingFee),
                FromMoney(response.ShippingDiscount),
                FromMoney(response.FinalShippingFee),
                FromMoney(response.FinalTotal)
            );
        } catch (Exception ex) {
            _logger.LogError(
                ex, "pricing did not answer; this order is priced from the cart alone, so any "
                  + "voucher or discount the customer expected is NOT applied");

            var finalFee = Math.Max(0m, baseShippingFee);
            return new PriceCalculationResult(subtotal, 0m, baseShippingFee, 0m, finalFee, subtotal + finalFee);
        }
    }

    private static Money ToMoney(decimal amount) {
        return new Money {
            AmountMinor = (long)Math.Round(amount * MinorPerMajor, MidpointRounding.AwayFromZero),
            Currency = "IDR"
        };
    }

    private static decimal FromMoney(Money? money) {
        return money == null ? 0m : (decimal)money.AmountMinor / MinorPerMajor;
    }
}
