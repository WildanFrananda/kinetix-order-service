namespace Kinetix.OrderService.Application.Exceptions;

public class CartSpansTwoMerchantsException(int merchantCount)
    : Exception(
        $"this cart holds items from {merchantCount} merchants, and an order belongs to one; "
      + "check out each merchant's items separately"
    ) {
    public int MerchantCount { get; } = merchantCount;
}
