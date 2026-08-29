using System.Globalization;
using Kinetix.OrderService.Grpc.Pricing;

namespace Kinetix.OrderService.Application.Services;

public class PricingGrpcClient(PricingService.PricingServiceClient client) : IPricingClient {
    private readonly PricingService.PricingServiceClient _client = client;

    public async Task<PriceCalculationResult> CalculatePriceAsync(string? voucherCode, decimal subtotal, decimal baseShippingFee) {
        if (subtotal <= 0m && baseShippingFee <= 0m) {
            return new PriceCalculationResult(0m, 0m, 0m, 0m, 0m, 0m);
        }

        try {
            var request = new CalculatePriceRequest {
                VoucherCode = voucherCode ?? string.Empty,
                BaseShippingFee = baseShippingFee.ToString("F2", CultureInfo.InvariantCulture)
            };

            request.Items.Add(new PriceItemRequest {
                ProductId = "CART-ITEM",
                BasePrice = subtotal.ToString("F2", CultureInfo.InvariantCulture),
                Quantity = 1
            });

            var response = await _client.CalculatePriceAsync(request);

            _ = decimal.TryParse(response.Subtotal, CultureInfo.InvariantCulture, out var respSubtotal);
            _ = decimal.TryParse(response.VoucherDiscount, CultureInfo.InvariantCulture, out var voucherDiscount);
            _ = decimal.TryParse(response.BaseShippingFee, CultureInfo.InvariantCulture, out var respBaseShipping);
            _ = decimal.TryParse(response.ShippingDiscount, CultureInfo.InvariantCulture, out var shippingDiscount);
            _ = decimal.TryParse(response.FinalShippingFee, CultureInfo.InvariantCulture, out var finalShippingFee);
            _ = decimal.TryParse(response.FinalTotal, CultureInfo.InvariantCulture, out var finalTotal);

            return new PriceCalculationResult(
                respSubtotal > 0m ? respSubtotal : subtotal,
                Math.Max(0m, voucherDiscount),
                respBaseShipping > 0m ? respBaseShipping : baseShippingFee,
                Math.Max(0m, shippingDiscount),
                Math.Max(0m, finalShippingFee),
                finalTotal > 0m ? finalTotal : Math.Max(0m, subtotal - voucherDiscount + finalShippingFee)
            );
        } catch {
            var finalFee = Math.Max(0m, baseShippingFee);
            return new PriceCalculationResult(subtotal, 0m, baseShippingFee, 0m, finalFee, subtotal + finalFee);
        }
    }
}
