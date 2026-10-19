namespace Kinetix.OrderService.Application.Exceptions;

public class ProductWeightUnknownException(IReadOnlyList<string> productIds)
    : Exception(
        $"{string.Join(", ", productIds)} has no weight in catalog, so the parcel cannot be weighed "
      + "and its shipping cannot be priced; its merchant must set one"
    ) {
    public IReadOnlyList<string> ProductIds { get; } = productIds;
}
