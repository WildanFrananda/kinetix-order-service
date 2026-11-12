using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Tests;

public sealed class TrackedDispatchClaims(OrderDbContext dbContext) : ICourierDispatchClaims {
    private readonly OrderDbContext _dbContext = dbContext;

    public async Task<bool> TryClaimAsync(string orderNumber, DateTime now, CancellationToken cancellationToken) {
        var due = await _dbContext.CourierDispatches.FirstOrDefaultAsync(
            d => d.OrderNumber == orderNumber && d.NextAttemptAt != null && d.NextAttemptAt <= now,
            cancellationToken
        );

        if (due is null) {
            return false;
        }

        due.NextAttemptAt = now.Add(CourierDispatchClaims.Lease);
        due.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }
}
