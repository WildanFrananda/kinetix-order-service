namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingNotServiceableException(IReadOnlyList<string> reasons)
    : Exception("matching marked every courier tier it lists unavailable") {

    public IReadOnlyList<string> Reasons { get; } = reasons;
}
