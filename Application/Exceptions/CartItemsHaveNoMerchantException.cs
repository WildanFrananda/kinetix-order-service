namespace Kinetix.OrderService.Application.Exceptions;

public class CartItemsHaveNoMerchantException()
    : Exception(
        "the items in this cart carry no merchant, so there is nobody to quote shipping for and "
      + "nobody to pay; remove them and add them again"
    );
