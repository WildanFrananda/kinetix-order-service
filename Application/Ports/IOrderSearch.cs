using Kinetix.OrderService.Application.Search;

namespace Kinetix.OrderService.Application.Ports;

public interface IOrderSearch {
    Task<OrderSearchPage> SearchAsync(OrderSearchQuery query, CancellationToken cancellationToken);
}
