using Kinetix.OrderService.Application.Results;

namespace Kinetix.OrderService.Application.Ports;


public interface IPricingClient {
    Task<PriceCalculationResult> CalculatePriceAsync(
        string? voucherCode, IReadOnlyList<PriceLine> lines, ShippingJourney? shipping
    );

    Task<IReadOnlyList<QuotedShippingResult>> QuoteShippingAsync(
        IReadOnlyList<ShippingJourney> journeys
    );
}
