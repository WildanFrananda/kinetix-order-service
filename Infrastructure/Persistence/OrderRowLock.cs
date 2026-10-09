using Kinetix.OrderService.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public class OrderRowLock(OrderDbContext dbContext) : IOrderRowLock {
    private readonly OrderDbContext _dbContext = dbContext;

    public async Task LockAsync(string orderNumber, CancellationToken cancellationToken) {
        if (_dbContext.Database.CurrentTransaction is null) {
            throw new InvalidOperationException(
                "an order row lock outside a transaction is released as soon as it is taken"
            );
        }

        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM orders WHERE order_number = {orderNumber} FOR UPDATE",
            cancellationToken
        );
    }
}
