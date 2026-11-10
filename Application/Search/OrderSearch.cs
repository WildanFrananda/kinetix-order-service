using Kinetix.OrderService.Application.Ports;

namespace Kinetix.OrderService.Application.Search;

public sealed class OrderSearch(IOrderSearchRepository repository) : IOrderSearch {
    private readonly IOrderSearchRepository _repository = repository;

    public async Task<OrderSearchPage> SearchAsync(OrderSearchQuery query, CancellationToken cancellationToken) {
        var rows = await _repository.NewestFirstAsync(query, query.Limit + 1, cancellationToken);

        if (rows.Count <= query.Limit) {
            return new OrderSearchPage(rows, null);
        }

        var page = rows.Take(query.Limit).ToList();
        var last = page[^1];

        return new OrderSearchPage(page, new OrderSearchCursor(last.CreatedAt, last.Id));
    }
}
