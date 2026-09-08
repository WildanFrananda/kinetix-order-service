namespace Kinetix.OrderService.Application.Checkout;

public record SagaReservation(string MerchantPrincipalId, string Sku, int Quantity);
