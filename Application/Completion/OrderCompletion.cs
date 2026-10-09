using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Completion;

public class OrderCompletion(
    OrderDbContext dbContext,
    IOrderRowLock rowLock,
    IEscrowClient escrowClient,
    ReturnWindow returnWindow,
    ILogger<OrderCompletion> logger
) : IOrderCompletion {
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IOrderRowLock _rowLock = rowLock;
    private readonly IEscrowClient _escrowClient = escrowClient;
    private readonly ReturnWindow _returnWindow = returnWindow;
    private readonly ILogger<OrderCompletion> _logger = logger;

    public async Task<CompletionOutcome> CompleteAsync(
        string orderNumber, bool waitForReturnWindow, CancellationToken cancellationToken
    ) {
        EscrowRelease release;

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken)) {
            await _rowLock.LockAsync(orderNumber, cancellationToken);

            var order = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, cancellationToken);

            var refusal = await RefusalAsync(order, waitForReturnWindow, cancellationToken);
            if (refusal is not null) {
                return refusal;
            }

            var now = DateTime.UtcNow;

            order!.Status = OrderStatus.COMPLETED;
            order.CompletedAt = now;
            order.UpdatedAt = now;

            release = new EscrowRelease {
                OrderNumber = orderNumber,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _dbContext.EscrowReleases.Add(release);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        _logger.LogInformation(
            "{Order} is complete; its escrow is now owed to the merchant", orderNumber
        );

        var released = await TryReleaseAsync(release, cancellationToken);
        return new CompletionOutcome(CompletionStatus.Completed, released, release.LastError);
    }

    public async Task<bool> TryReleaseAsync(EscrowRelease release, CancellationToken cancellationToken) {
        release.Attempts += 1;
        release.UpdatedAt = DateTime.UtcNow;

        try {
            var result = await _escrowClient.ReleaseHoldAsync(release.OrderNumber);

            if (result.Success) {
                release.ReleasedAt = DateTime.UtcNow;
                release.NextAttemptAt = null;
                release.LastError = null;
            } else {
                Halt(release, result.Detail ?? "payment refused the release");
            }
        } catch (RpcException e) when (CompensationPolicy.Classify(e) is CompensationFailureKind.Terminal) {
            Halt(release, e.Status.Detail);
        } catch (Exception e) when (e is not OperationCanceledException) {
            Defer(release, e.Message);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return release.ReleasedAt is not null;
    }

    private async Task<CompletionOutcome?> RefusalAsync(
        OrderEntity? order, bool waitForReturnWindow, CancellationToken cancellationToken
    ) {
        if (order is null) {
            return Refused(CompletionStatus.NoSuchOrder, "no order carries that number");
        }

        if (order.Status is OrderStatus.COMPLETED) {
            return Refused(CompletionStatus.AlreadyCompleted, null);
        }

        if (order.Status is not OrderStatus.DELIVERED) {
            return Refused(
                CompletionStatus.NotDelivered,
                $"the order is {order.Status}; only a delivered order can be completed"
            );
        }

        if (waitForReturnWindow) {
            if (order.DeliveredAt is not { } deliveredAt) {
                return Refused(
                    CompletionStatus.WindowStillOpen,
                    "no delivery time is recorded, so the return window cannot be said to have closed"
                );
            }

            if (DateTime.UtcNow < _returnWindow.ClosesAt(deliveredAt)) {
                return Refused(
                    CompletionStatus.WindowStillOpen,
                    $"the return window closes at {_returnWindow.ClosesAt(deliveredAt):O}"
                );
            }
        }

        var unresolved = await _dbContext.OrderReturns.AnyAsync(
            r => r.OrderNumber == order.OrderNumber
                && (r.Status == ReturnStatus.OPEN || r.Status == ReturnStatus.GOODS_RECEIVED),
            cancellationToken
        );

        if (unresolved) {
            return Refused(
                CompletionStatus.ReturnUnresolved,
                "a return against this order is not resolved, so the buyer may still be owed a refund"
            );
        }

        return null;
    }

    private void Halt(EscrowRelease release, string reason) {
        release.LastError = reason.Length <= 500 ? reason : reason[..500];
        release.NextAttemptAt = null;

        _logger.LogError(
            "the escrow for {Order} is owed to the merchant and payment refused to release it: {Reason}. "
          + "It is not retried until someone resolves it.",
            release.OrderNumber, release.LastError
        );
    }

    private void Defer(EscrowRelease release, string reason) {
        var backoff = TimeSpan.FromMinutes(Math.Min(release.Attempts, MaxBackoff.TotalMinutes));

        release.LastError = reason.Length <= 500 ? reason : reason[..500];
        release.NextAttemptAt = DateTime.UtcNow.Add(backoff);

        _logger.LogWarning(
            "the escrow for {Order} is owed to the merchant and still held after {Attempts} attempt(s): "
          + "{Reason}. Next attempt in {Backoff}.",
            release.OrderNumber, release.Attempts, release.LastError, backoff
        );
    }

    private static CompletionOutcome Refused(CompletionStatus status, string? detail) =>
        new(status, false, detail);
}
