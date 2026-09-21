using Common.V1;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using Pricing.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class PricingGrpcClient(
    PricingService.PricingServiceClient client,
    ILogger<PricingGrpcClient> logger
) : IPricingClient {
    private readonly PricingService.PricingServiceClient _client = client;
    private readonly ILogger<PricingGrpcClient> _logger = logger;

    private const long MinorPerMajor = 100L;

    public async Task<IReadOnlyList<QuotedShippingResult>> QuoteShippingAsync(
        IReadOnlyList<ShippingJourney> journeys
    ) {

        if (journeys.Count == 0) {
            return [];
        }

        try {
            var request = new QuoteShippingRequest();

            foreach (var journey in journeys) {
                request.Journeys.Add(ToJourney(journey));
            }

            var response = await _client.QuoteShippingAsync(request);

            return [.. response.Quotes.Select(quote => new QuotedShippingResult(
                quote.ServiceTier,
                quote.BaseShippingFee is null ? 0m : FromMoney(quote.BaseShippingFee),
                quote.Priced
            ))];
        } catch (Exception ex) {
            _logger.LogError(
                ex, "pricing did not answer QuoteShipping, so this checkout is refused rather "
                  + "than shipped at a fee nobody quoted"
            );

            throw new PricingUnavailableException(ex);
        }
    }

    private static Pricing.V1.ShippingQuoteRequest ToJourney(ShippingJourney journey) =>
        new() {
            ServiceTier = journey.ServiceTier,
            DistanceKm = journey.DistanceKm,
            TotalWeightGrams = journey.TotalWeightGrams
        };

    public async Task<PriceCalculationResult> CalculatePriceAsync(
        string? voucherCode, IReadOnlyList<PriceLine> lines, ShippingJourney? shipping
    ) {

        if (lines.Count == 0 && shipping is null) {
            return new PriceCalculationResult(0m, 0m, 0m, 0m, 0m, 0m, []);
        }

        try {
            var request = new CalculatePriceRequest {
                VoucherCode = voucherCode ?? string.Empty
            };

            if (shipping is not null) {
                request.Shipping = ToJourney(shipping);
            }

            foreach (var line in lines) {
                request.Items.Add(new PriceItemRequest {
                    ProductId = line.ProductId,
                    CategoryId = line.CategoryId ?? string.Empty,
                    BasePrice = ToMoney(line.UnitPrice),
                    Quantity = line.Quantity
                });
            }

            var response = await _client.CalculatePriceAsync(request);

            return new PriceCalculationResult(
                FromMoney(response.Subtotal),
                FromMoney(response.VoucherDiscount),
                FromMoney(response.BaseShippingFee),
                FromMoney(response.ShippingDiscount),
                FromMoney(response.FinalShippingFee),
                FromMoney(response.FinalTotal),
                [.. response.Items.Select(item => new PricedLine(
                    item.ProductId,
                    item.Quantity,
                    string.IsNullOrWhiteSpace(item.AppliedFlashSale) ? null : item.AppliedFlashSale
                ))]
            );
        } catch (Exception ex) {
            _logger.LogError(
                ex, "pricing did not answer, so this checkout is refused rather than priced at "
                  + "list; any voucher, discount or flash sale the customer expected cannot be "
                  + "verified from here");

            throw new PricingUnavailableException(ex);
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
