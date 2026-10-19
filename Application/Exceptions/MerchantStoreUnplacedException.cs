namespace Kinetix.OrderService.Application.Exceptions;

public class MerchantStoreUnplacedException(string merchantPrincipalId)
    : Exception(
        $"identity has no point on the map for merchant {merchantPrincipalId}'s store, so the distance "
      + "a courier would travel cannot be measured"
    ) {
    public string MerchantPrincipalId { get; } = merchantPrincipalId;
}
