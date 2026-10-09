namespace Kinetix.OrderService.Application.Exceptions;

public class MerchantMayNotSellException(string merchantPrincipalId)
    : Exception($"identity does not permit merchant {merchantPrincipalId} to sell, so this cart cannot be bought now") {
    public string MerchantPrincipalId { get; } = merchantPrincipalId;
}
