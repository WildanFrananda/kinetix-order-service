using Kinetix.OrderService.Application.Ports;

namespace Kinetix.OrderService.Application.Checkout;

public record ShippingRoute(MapPoint Pickup, MapPoint DropOff);
