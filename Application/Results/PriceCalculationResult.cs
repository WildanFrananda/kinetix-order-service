namespace Kinetix.OrderService.Application.Results;

public record PriceCalculationResult(
    decimal Subtotal,
    decimal VoucherDiscount,
    decimal BaseShippingFee,
    decimal ShippingDiscount,
    decimal FinalShippingFee,
    decimal FinalTotal,
    IReadOnlyList<PricedLine> Lines
);
