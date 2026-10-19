using Kinetix.OrderService.Application.Results;

namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingAddressUnplacedException(AddressPlacementFailure failure, string detail)
    : Exception($"the shipping address could not be placed on the map ({failure}): {detail}") {
    public AddressPlacementFailure Failure { get; } = failure;

    public string Detail { get; } = detail;
}
