namespace Kinetix.OrderService.Application.Services;

public record CheckoutPlan(
    string OrderNumber,
    string CustomerPrincipalId,
    string? VoucherCode,
    IReadOnlyList<SagaReservation> Reservations,
    IReadOnlyList<FlashSaleClaim> FlashSaleClaims,
    string MerchantPrincipalId,
    decimal TotalOrderAmount,
    decimal MerchantAmount,
    decimal ShippingFeeAmount
);
