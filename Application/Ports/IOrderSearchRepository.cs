using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.DTOs.Responses;

namespace Kinetix.OrderService.Application.Ports;

public interface IOrderSearchRepository {
    Task<IReadOnlyList<OrderSummaryResponse>> NewestFirstAsync(
        OrderSearchQuery query,
        int take,
        CancellationToken cancellationToken
    );
}
