using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Faults;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Kinetix.OrderService.Application.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Application.Returns;

public class ReturnRefunds(
    OrderDbContext dbContext,
    IEscrowClient escrowClient,
    ILogger<ReturnRefunds> logger
) : IReturnRefunds {
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IEscrowClient _escrowClient = escrowClient;
    private readonly ILogger<ReturnRefunds> _logger = logger;

    public static string IdempotencyKeyFor(string returnNumber) => $"return:{returnNumber}";

    public async Task<bool> TryRefundAsync(OrderReturn record, CancellationToken cancellationToken) {
        if (record.Status is not ReturnStatus.GOODS_RECEIVED || record.RefundAmount is not { } amount) {
            return record.Status is ReturnStatus.RESOLVED;
        }

        var buyer = await _dbContext.Orders
            .Where(o => o.OrderNumber == record.OrderNumber)
            .Select(o => o.CustomerPrincipalId)
            .FirstAsync(cancellationToken);

        record.RefundAttempts += 1;
        record.UpdatedAt = DateTime.UtcNow;

        if (amount <= 0m) {
            Resolve(record, buyer);
        } else {
            try {
                var result = await _escrowClient.RefundGoodsAsync(
                    record.OrderNumber, amount, record.Reason, IdempotencyKeyFor(record.ReturnNumber)
                );

                if (result.Success) {
                    Resolve(record, buyer);
                } else {
                    Halt(record, result.Detail ?? "payment refused the refund");
                }
            } catch (RpcException e) when (CompensationPolicy.Classify(e) is CompensationFailureKind.Terminal) {
                Halt(record, e.Status.Detail);
            } catch (Exception e) when (e is not OperationCanceledException) {
                Defer(record, e);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return record.Status is ReturnStatus.RESOLVED;
    }

    private void Resolve(OrderReturn record, string buyer) {
        var now = DateTime.UtcNow;

        record.ResolvedAt = now;
        record.NextRefundAttemptAt = null;
        record.LastRefundError = null;
        _dbContext.ChangeReturnStatus(record, ReturnStatus.RESOLVED, buyer, LifecycleActor.System, now);

        _logger.LogInformation(
            "{Return} refunded {Amount} to the buyer of {Order}",
            record.ReturnNumber, record.RefundAmount, record.OrderNumber
        );
    }

    private void Halt(OrderReturn record, string reason) {
        record.LastRefundError = reason.Length <= 500 ? reason : reason[..500];
        record.NextRefundAttemptAt = null;

        _logger.LogError(
            "{Return} owes the buyer of {Order} {Amount} and payment refused it: {Reason}. It is not "
          + "retried, and the order is not completed until someone resolves it.",
            record.ReturnNumber, record.OrderNumber, record.RefundAmount, record.LastRefundError
        );
    }

    private void Defer(OrderReturn record, Exception failure) {
        var backoff = TimeSpan.FromMinutes(Math.Min(record.RefundAttempts, MaxBackoff.TotalMinutes));

        record.LastRefundError = FailureReason.Of(CalledService.Payment, failure);
        record.NextRefundAttemptAt = DateTime.UtcNow.Add(backoff);

        _logger.LogWarning(
            failure,
            "{Return} still owes the buyer of {Order} {Amount} after {Attempts} attempt(s): {Reason}. "
          + "Next attempt in {Backoff}.",
            record.ReturnNumber, record.OrderNumber, record.RefundAmount, record.RefundAttempts,
            record.LastRefundError, backoff
        );
    }
}
