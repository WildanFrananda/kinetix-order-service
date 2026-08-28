using Kinetix.OrderService.Grpc.Pricing;

namespace Kinetix.OrderService.Application.Services;

public class PricingGrpcClient(PricingService.PricingServiceClient client) : IPricingClient {
    private readonly PricingService.PricingServiceClient _client = client;

    public async Task<decimal> CalculateVoucherDiscountAsync(string voucherCode, decimal subtotal) {
        if (string.IsNullOrWhiteSpace(voucherCode) || subtotal <= 0m) {
            return 0m;
        }

        try {
            var request = new CalculatePriceRequest {
                VoucherCode = voucherCode
            };
            request.Items.Add(new PriceItemRequest {
                ProductId = "CART-ITEM",
                BasePrice = subtotal.ToString("F2"),
                Quantity = 1
            });

            var response = await _client.CalculatePriceAsync(request);
            if (decimal.TryParse(response.VoucherDiscount, System.Globalization.CultureInfo.InvariantCulture, out var voucherDiscount)) {
                return Math.Max(0m, voucherDiscount);
            }
            return 0m;
        } catch {
            return 0m;
        }
    }
}
