using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Application.Checkout;

public class CompletedCheckoutFinisher(
    OrderDbContext dbContext,
    IOrderRowLock rowLock,
    ILogger<CompletedCheckoutFinisher> logger
) {
    public const string BeforeFulfillmentReason =
        "completed by a release before fulfillment existed; the escrow is held and no warehouse task exists. "
          + "Decide refund or fulfil";

    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IOrderRowLock _rowLock = rowLock;
    private readonly ILogger<CompletedCheckoutFinisher> _logger = logger;

    public async Task<int> FinishAsync(TimeSpan grace, int batchSize, CancellationToken cancellationToken) {
        var completedBefore = DateTime.UtcNow - grace;

        var unpaid = await _dbContext.Orders
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.PENDING_PAYMENT)
            .Join(
                _dbContext.CheckoutSagas.Where(s =>
                    s.State == SagaState.Completed
                    && s.UpdatedAt < completedBefore
                    && s.NeedsAttentionAt == null
                ),
                order => order.OrderNumber,
                saga => saga.OrderNumber,
                (order, saga) => order.OrderNumber
            )
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var finished = 0;
        foreach (var orderNumber in unpaid) {
            if (await FinishOneAsync(orderNumber, cancellationToken)) {
                finished += 1;
            }
        }

        return finished;
    }

    public static IReadOnlyList<string> MissingFromACompletedCheckout(IReadOnlyList<CheckoutSagaStep> steps) {
        var missing = new List<string>();

        if (!steps.Any(s => s.Name == SagaStepName.ReserveStock)) {
            missing.Add($"a {nameof(SagaStepName.ReserveStock)} step");
        }

        if (steps.Count(s => s.Name == SagaStepName.CreateEscrowHold) != 1) {
            missing.Add($"exactly one {nameof(SagaStepName.CreateEscrowHold)} step");
        }

        if (steps.Count(s => s.Name == SagaStepName.CreateFulfillmentOrder) != 1) {
            missing.Add($"exactly one {nameof(SagaStepName.CreateFulfillmentOrder)} step");
        }

        missing.AddRange(steps
            .Where(s => s.State != SagaStepState.Done)
            .Select(s => $"{s.Name} {s.Reference} finished (it is {s.State})")
        );

        return missing;
    }

    private async Task<bool> FinishOneAsync(string orderNumber, CancellationToken cancellationToken) {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _rowLock.LockAsync(orderNumber, cancellationToken);

        var order = await _dbContext.Orders
            .FirstOrDefaultAsync(
                o => o.OrderNumber == orderNumber && o.Status == OrderStatus.PENDING_PAYMENT,
                cancellationToken
            );
        var saga = await _dbContext.CheckoutSagas
            .FirstOrDefaultAsync(
                s => s.OrderNumber == orderNumber && s.State == SagaState.Completed && s.NeedsAttentionAt == null,
                cancellationToken
            );
        if (order is null || saga is null) {
            return false;
        }

        var steps = await _dbContext.CheckoutSagaSteps
            .AsNoTracking()
            .Where(s => s.SagaId == saga.Id)
            .ToListAsync(cancellationToken);
        var missing = MissingFromACompletedCheckout(steps);

        var now = DateTime.UtcNow;

        if (missing.Count > 0) {
            saga.NeedsAttentionAt = now;
            saga.LastFailureCode = CompensationFailureCode.CompletedWithoutFulfillment;
            saga.FailureReason = steps.Any(s => s.Name == SagaStepName.CreateFulfillmentOrder)
                ? $"completed without {string.Join("; ", missing)}. Decide refund or fulfil"
                : BeforeFulfillmentReason;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogError(
                "{Order}'s checkout saga is Completed but lacks {Missing}, so the order stays PENDING_PAYMENT "
                  + "and the saga waits in the attention queue: {Reason}",
                orderNumber,
                string.Join("; ", missing),
                saga.FailureReason
            );

            return false;
        }

        _dbContext.ChangeStatus(order, OrderStatus.PAID, LifecycleActor.System, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogWarning(
            "{Order}'s checkout saga completed, with the escrow held and the warehouse told, but the order "
              + "was never marked PAID; it is PAID now. The buyer's request most likely failed after the "
              + "saga finished, so their cart may still hold these items",
            orderNumber
        );

        return true;
    }
}
