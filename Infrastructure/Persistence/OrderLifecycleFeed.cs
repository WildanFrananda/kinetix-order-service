using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public class OrderLifecycleFeed(OrderDbContext dbContext) : IOrderLifecycleFeed {
    private readonly OrderDbContext _dbContext = dbContext;

    public async Task<LifecyclePage> ReadAfterAsync(
        ulong transactionId, long sequence, int limit, CancellationToken cancellationToken
    ) {
        var after = transactionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var take = limit + 1;

        var rows = await _dbContext.OrderLifecycleEvents
            .FromSqlInterpolated($@"
                SELECT *
                FROM order_lifecycle_events
                WHERE transaction_id < pg_snapshot_xmin(pg_current_snapshot())
                  AND (transaction_id, sequence) > (CAST({after} AS xid8), {sequence})
                ORDER BY transaction_id, sequence
                LIMIT {take}")
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limit;

        return new LifecyclePage(hasMore ? rows[..limit] : rows, hasMore);
    }
}
