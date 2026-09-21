namespace Kinetix.OrderService.Application.Ports;

public interface IAddressDirectory {
    Task<MapPoint?> PickupPointAsync(string merchantPrincipalId);

    Task<MapPoint?> DeliveryPointAsync(string customerPrincipalId);
}
