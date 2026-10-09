using Kinetix.OrderService.Domain.Entities;

namespace Kinetix.OrderService.Application.Ports;

public interface IReturnRefunds {
    Task<bool> TryRefundAsync(OrderReturn record, CancellationToken cancellationToken);
}
