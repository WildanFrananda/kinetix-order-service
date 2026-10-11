using Kinetix.OrderService.Domain.Entities;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Ports;

public interface ICourierDispatcher {
    Task AttemptAsync(CourierDispatch dispatch, OrderEntity order);
}
