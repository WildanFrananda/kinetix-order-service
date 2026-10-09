using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Application.Returns;

public class ReturnRejection(
    OrderDbContext dbContext,
    IOrderRowLock rowLock,
    ILogger<ReturnRejection> logger
) : IReturnRejection {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IOrderRowLock _rowLock = rowLock;
    private readonly ILogger<ReturnRejection> _logger = logger;

    public async Task<ReturnRejectionStatus> RejectAsync(
        string returnNumber, string reason, CancellationToken cancellationToken
    ) {
        if (string.IsNullOrWhiteSpace(reason)) {
            return ReturnRejectionStatus.NoReasonGiven;
        }

        var orderNumber = await _dbContext.OrderReturns
            .AsNoTracking()
            .Where(r => r.ReturnNumber == returnNumber)
            .Select(r => r.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (orderNumber is null) {
            return ReturnRejectionStatus.NoSuchReturn;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _rowLock.LockAsync(orderNumber, cancellationToken);

        var record = await _dbContext.OrderReturns
            .FirstAsync(r => r.ReturnNumber == returnNumber, cancellationToken);

        if (record.Status is ReturnStatus.REJECTED) {
            return ReturnRejectionStatus.AlreadyRejected;
        }

        if (record.Status is not ReturnStatus.OPEN || record.GoodsReceivedAt is not null) {
            return ReturnRejectionStatus.GoodsAlreadyReceived;
        }

        var now = DateTime.UtcNow;
        var stated = reason.Trim();

        record.Status = ReturnStatus.REJECTED;
        record.RejectedAt = now;
        record.RejectionReason = stated.Length <= 500 ? stated : stated[..500];
        record.UpdatedAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "{Return} against {Order} was rejected by an admin: {Reason}",
            returnNumber, orderNumber, record.RejectionReason
        );

        return ReturnRejectionStatus.Rejected;
    }
}
