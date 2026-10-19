using Kinetix.OrderService.Application.Ports;

namespace Kinetix.OrderService.Application.Results;

public record AddressPlacement(MapPoint? Point, AddressPlacementFailure? Failure, string Detail) {
    public static AddressPlacement Placed(MapPoint point) => new(point, null, string.Empty);

    public static AddressPlacement Refused(AddressPlacementFailure failure, string detail) => new(null, failure, detail);
}
