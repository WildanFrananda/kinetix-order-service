namespace Kinetix.OrderService.Application.Services;

public record SagaReservation(string MerchantPrincipalId, string Sku, int Quantity);
