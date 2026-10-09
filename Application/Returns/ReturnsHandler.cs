using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Returns;

public class ReturnsHandler(
    OrderDbContext dbContext,
    IOrderRowLock rowLock,
    IReturnRefunds refunds,
    ILogger<ReturnsHandler> logger
) : IReturnsHandler {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IOrderRowLock _rowLock = rowLock;
    private readonly IReturnRefunds _refunds = refunds;
    private readonly ILogger<ReturnsHandler> _logger = logger;

    public async Task<OpenReturnOutcome> OpenAsync(
        string orderNumber, string merchantPrincipalId, string reason
    ) {
        if (string.IsNullOrWhiteSpace(orderNumber)) {
            return Refused("order_number is required: a return belongs to a named order");
        }

        if (string.IsNullOrWhiteSpace(merchantPrincipalId)) {
            return Refused("merchant_principal_id is required");
        }

        if (string.IsNullOrWhiteSpace(reason)) {
            return Refused("reason is required: a return with no stated reason cannot be settled");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();
        await _rowLock.LockAsync(orderNumber, CancellationToken.None);

        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order is null) {
            return Refused("no order carries that number");
        }

        if (!string.Equals(order.MerchantPrincipalId, merchantPrincipalId, StringComparison.Ordinal)) {
            return Refused("that order is not that merchant's");
        }

        var existing = await _dbContext.OrderReturns
            .FirstOrDefaultAsync(r => r.OrderNumber == orderNumber);

        if (existing is not null) {
            return new OpenReturnOutcome(true, existing.ReturnNumber, existing.Status, true, null);
        }

        if (order.Status is OrderStatus.COMPLETED) {
            return Refused("the return window for that order has closed and the merchant has been paid");
        }

        if (order.Status is not OrderStatus.DELIVERED) {
            return Refused($"that order is {order.Status}; a return is opened only against a delivered order");
        }

        var now = DateTime.UtcNow;
        var opened = new OrderReturn {
            ReturnNumber = $"RMA-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            OrderNumber = orderNumber,
            MerchantPrincipalId = merchantPrincipalId,
            Reason = reason.Length > 500 ? reason[..500] : reason,
            Status = ReturnStatus.OPEN,
            OpenedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _dbContext.OrderReturns.Add(opened);
        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation(
            "{Return} opened against {Order} for {Merchant}", opened.ReturnNumber, orderNumber, merchantPrincipalId
        );

        return new OpenReturnOutcome(true, opened.ReturnNumber, opened.Status, false, null);
    }

    public async Task<ReturnGoodsReceivedOutcome> GoodsReceivedAsync(
        string returnNumber,
        string merchantPrincipalId,
        IReadOnlyList<ReturnedLine> lines,
        string binCode,
        DateTime receivedAt
    ) {
        if (string.IsNullOrWhiteSpace(returnNumber)) {
            return NotRecorded("return_number is required");
        }

        var orderNumber = await _dbContext.OrderReturns
            .AsNoTracking()
            .Where(r => r.ReturnNumber == returnNumber)
            .Select(r => r.OrderNumber)
            .FirstOrDefaultAsync();

        if (orderNumber is null) {
            return NotRecorded("no return carries that number");
        }

        OrderReturn record;

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync()) {
            await _rowLock.LockAsync(orderNumber, CancellationToken.None);

            record = await _dbContext.OrderReturns
                .Include(r => r.Lines)
                .FirstAsync(r => r.ReturnNumber == returnNumber);

            if (!string.Equals(record.MerchantPrincipalId, merchantPrincipalId, StringComparison.Ordinal)) {
                return NotRecorded("that return belongs to another merchant");
            }

            if (record.GoodsReceivedAt is not null) {
                return new ReturnGoodsReceivedOutcome(true, true, record.Status, null);
            }

            if (record.Status is ReturnStatus.REJECTED) {
                return NotRecorded("that return was rejected, so goods against it are not expected");
            }

            var usable = lines
                .Where(line => !string.IsNullOrWhiteSpace(line.Sku) && line.Quantity > 0)
                .ToList();

            if (usable.Count == 0) {
                return NotRecorded(
                    "no line names a SKU and a quantity above zero, so nothing is recorded as returned"
                );
            }

            var order = await _dbContext.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);

            if (order is null) {
                return NotRecorded("the order this return was opened against no longer exists");
            }

            var fault = LinesFault(order, usable);
            if (fault is not null) {
                return NotRecorded(fault);
            }

            var now = DateTime.UtcNow;

            record.Status = ReturnStatus.GOODS_RECEIVED;
            record.GoodsReceivedAt = receivedAt == default ? now : receivedAt;
            record.BinCode = string.IsNullOrWhiteSpace(binCode) ? null : binCode;
            record.RefundAmount = ProRataRefund(order, usable);
            record.UpdatedAt = now;

            if (order.Status is OrderStatus.DELIVERED) {
                record.NextRefundAttemptAt = now;
            } else {
                record.LastRefundError =
                    $"the order is {order.Status}, so its escrow can no longer refund the buyer";
                _logger.LogError(
                    "{Return} received goods for {Order}, which is {Status}; the buyer is owed {Amount} "
                  + "that escrow cannot pay",
                    record.ReturnNumber, orderNumber, order.Status, record.RefundAmount
                );
            }

            foreach (var line in usable) {
                record.Lines.Add(new OrderReturnLine {
                    ReturnNumber = record.ReturnNumber,
                    Sku = line.Sku,
                    Quantity = line.Quantity,
                });
            }

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation(
                "{Return} has its goods back: {Lines} line(s) in bin {Bin}, {Amount} owed to the buyer",
                record.ReturnNumber, usable.Count, record.BinCode ?? "(unstated)", record.RefundAmount
            );
        }

        if (record.NextRefundAttemptAt is not null) {
            await _refunds.TryRefundAsync(record, CancellationToken.None);
        }

        return new ReturnGoodsReceivedOutcome(true, false, record.Status, null);
    }

    private static decimal ProRataRefund(OrderEntity order, IReadOnlyList<ReturnedLine> lines) {
        var ordered = Ordered(order);
        decimal goodsValue = ordered.Values.Sum(o => o.Value);
        decimal merchantShare = order.FinalTotal - order.FinalShippingFee;

        if (goodsValue <= 0m || merchantShare <= 0m) {
            return 0m;
        }

        decimal returnedValue = lines
            .GroupBy(line => line.Sku, StringComparer.Ordinal)
            .Sum(group => {
                var (quantity, value) = ordered[group.Key];
                return value * group.Sum(line => (long)line.Quantity) / quantity;
            });

        decimal refund = decimal.Floor(merchantShare * returnedValue / goodsValue * 100m) / 100m;
        return Math.Min(refund, merchantShare);
    }

    private static string? LinesFault(OrderEntity order, IReadOnlyList<ReturnedLine> lines) {
        var ordered = Ordered(order);

        foreach (var group in lines.GroupBy(line => line.Sku, StringComparer.Ordinal)) {
            long returned = group.Sum(line => (long)line.Quantity);

            if (!ordered.TryGetValue(group.Key, out var bought)) {
                return $"{group.Key} is not on order {order.OrderNumber}, so it cannot be returned against it";
            }

            if (returned > bought.Quantity) {
                return $"{returned} of {group.Key} came back but order {order.OrderNumber} bought {bought.Quantity}";
            }
        }

        return null;
    }

    private static Dictionary<string, (long Quantity, decimal Value)> Ordered(OrderEntity order) =>
        order.Items
            .GroupBy(item => item.ProductId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (group.Sum(item => (long)item.Quantity), group.Sum(item => item.LineSubtotal)),
                StringComparer.Ordinal
            );

    private static OpenReturnOutcome Refused(string fault) =>
        new(false, string.Empty, ReturnStatus.OPEN, false, fault);

    private static ReturnGoodsReceivedOutcome NotRecorded(string fault) =>
        new(false, false, ReturnStatus.OPEN, fault);
}
