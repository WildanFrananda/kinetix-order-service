namespace Kinetix.OrderService.Application.Exceptions;

public class MerchantStandingUnknownException(string merchantPrincipalId, Exception cause)
    : Exception($"identity did not answer whether merchant {merchantPrincipalId} may sell", cause) {
    public string MerchantPrincipalId { get; } = merchantPrincipalId;
}
