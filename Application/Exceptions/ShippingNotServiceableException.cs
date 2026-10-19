namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingNotServiceableException(IReadOnlyList<string> reasons)
    : Exception($"no courier tier can carry this parcel on this route: {string.Join("; ", reasons)}") {
    public IReadOnlyList<string> Reasons { get; } = reasons;
}
