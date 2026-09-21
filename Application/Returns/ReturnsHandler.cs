using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;

namespace Kinetix.OrderService.Application.Returns;

public class ReturnsHandler(
    OrderDbContext dbContext,
    ILogger<ReturnsHandler> logger
) : IReturnsHandler {
    private readonly OrderDbContext _dbContext = dbContext;
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

        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order is null) {
            return Refused("no order carries that number");
        }

        var existing = await _dbContext.OrderReturns
            .FirstOrDefaultAsync(r => r.OrderNumber == orderNumber);

        if (existing is not null) {
            return new OpenReturnOutcome(true, existing.ReturnNumber, existing.Status, true, null);
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

        try {
            await _dbContext.SaveChangesAsync();
        } catch (DbUpdateException) {
            _dbContext.Entry(opened).State = EntityState.Detached;

            var won = await _dbContext.OrderReturns
                .FirstOrDefaultAsync(r => r.OrderNumber == orderNumber);

            if (won is null) {
                throw;
            }

            return new OpenReturnOutcome(true, won.ReturnNumber, won.Status, true, null);
        }

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

        var record = await _dbContext.OrderReturns
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.ReturnNumber == returnNumber);

        if (record is null) {
            return NotRecorded("no return carries that number");
        }

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

        var now = DateTime.UtcNow;

        record.Status = ReturnStatus.GOODS_RECEIVED;
        record.GoodsReceivedAt = receivedAt == default ? now : receivedAt;
        record.BinCode = string.IsNullOrWhiteSpace(binCode) ? null : binCode;
        record.UpdatedAt = now;

        foreach (var line in usable) {
            record.Lines.Add(new OrderReturnLine {
                ReturnNumber = record.ReturnNumber,
                Sku = line.Sku,
                Quantity = line.Quantity,
            });
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "{Return} has its goods back: {Lines} line(s) in bin {Bin}",
            record.ReturnNumber, usable.Count, record.BinCode ?? "(unstated)"
        );

        return new ReturnGoodsReceivedOutcome(true, false, record.Status, null);
    }

    private static OpenReturnOutcome Refused(string fault) =>
        new(false, string.Empty, ReturnStatus.OPEN, false, fault);

    private static ReturnGoodsReceivedOutcome NotRecorded(string fault) =>
        new(false, false, ReturnStatus.OPEN, fault);
}
