using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Application.Checkout;

public class SagalessCheckoutCanceller(
    OrderDbContext dbContext,
    IOrderRowLock rowLock,
    ILogger<SagalessCheckoutCanceller> logger
) {
    public const string Reason =
        "the checkout that placed this order stopped before it started its saga, so nothing was reserved or charged";

    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IOrderRowLock _rowLock = rowLock;
    private readonly ILogger<SagalessCheckoutCanceller> _logger = logger;

    public async Task<int> CancelAsync(TimeSpan grace, int batchSize, CancellationToken cancellationToken) {
        var placedBefore = DateTime.UtcNow - grace;

        var sagaless = await _dbContext.Orders
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.PENDING_PAYMENT && o.CreatedAt < placedBefore)
            .Where(o => !_dbContext.CheckoutSagas.Any(s => s.OrderNumber == o.OrderNumber))
            .OrderBy(o => o.CreatedAt)
            .Select(o => o.OrderNumber)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var cancelled = 0;
        foreach (var orderNumber in sagaless) {
            if (await CancelOneAsync(orderNumber, cancellationToken)) {
                cancelled += 1;
            }
        }

        return cancelled;
    }

    private async Task<bool> CancelOneAsync(string orderNumber, CancellationToken cancellationToken) {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _rowLock.LockAsync(orderNumber, cancellationToken);

        var order = await _dbContext.Orders
            .FirstOrDefaultAsync(
                o => o.OrderNumber == orderNumber && o.Status == OrderStatus.PENDING_PAYMENT,
                cancellationToken
            );
        var started = await _dbContext.CheckoutSagas.AnyAsync(s => s.OrderNumber == orderNumber, cancellationToken);
        if (order is null || started) {
            return false;
        }

        var now = DateTime.UtcNow;
        _dbContext.CheckoutSagas.Add(new CheckoutSaga {
            OrderNumber = orderNumber,
            CustomerPrincipalId = order.CustomerPrincipalId,
            State = SagaState.Compensated,
            FailureReason = Reason,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _dbContext.ChangeStatus(order, OrderStatus.CANCELLED, LifecycleActor.System, now);

        try {
            await _dbContext.SaveChangesAsync(cancellationToken);
        } catch (DbUpdateException e) when (UniqueViolation.On(e, OrderDbContext.SagaOrderNumberIndex)) {
            _dbContext.ChangeTracker.Clear();
            _logger.LogInformation(
                "{Order}'s checkout started its saga while the order was being cancelled; it is left to that checkout",
                orderNumber
            );

            return false;
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogWarning(
            "{Order} sat in PENDING_PAYMENT with no checkout saga, so the checkout that placed it stopped before "
              + "reserving or charging anything; it is CANCELLED now and its Idempotency-Key replays as rolled back",
            orderNumber
        );

        return true;
    }
}
