namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingFeeContradictedException(decimal quotedBase, decimal pricingBase, decimal pricingFinal)
    : Exception(
        $"pricing answered with a shipping base of {pricingBase} and a final of {pricingFinal} "
      + $"against a quoted base of {quotedBase}; the fee that would be debited cannot be traced "
      + "back to matching's quote"
    ) {
    public decimal QuotedBase { get; } = quotedBase;
    public decimal PricingBase { get; } = pricingBase;
    public decimal PricingFinal { get; } = pricingFinal;
}
