namespace Kinetix.OrderService.Application.Results;

public record QuotedShippingResult(
    string ServiceTier,
    decimal BaseShippingFee,
    bool Priced
);
