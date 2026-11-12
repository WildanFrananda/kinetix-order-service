using Kinetix.OrderService.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public class CourierDispatchClaims(OrderDbContext dbContext) : ICourierDispatchClaims {
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private readonly OrderDbContext _dbContext = dbContext;

    public async Task<bool> TryClaimAsync(string orderNumber, DateTime now, CancellationToken cancellationToken) {
        var claimedUntil = now.Add(Lease);

        var claimed = await _dbContext.CourierDispatches
            .Where(d => d.OrderNumber == orderNumber && d.NextAttemptAt != null && d.NextAttemptAt <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(d => d.NextAttemptAt, claimedUntil)
                    .SetProperty(d => d.UpdatedAt, now),
                cancellationToken
            );

        return claimed == 1;
    }
}
