using Kinetix.OrderService.Application.Results;

namespace Kinetix.OrderService.Application.Ports;

public interface IAddressGeocoder {
    Task<AddressPlacement> PlaceAsync(string address, CancellationToken cancellationToken = default);
}
