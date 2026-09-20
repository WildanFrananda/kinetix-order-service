using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Persistence;
using DomainStatus = Kinetix.OrderService.Domain.Enums.OrderStatus;

namespace Kinetix.OrderService.Application.Delivery;

public class OrderDeliveredHandler(
    OrderDbContext dbContext,
    IEscrowClient escrowClient,
    ILogger<OrderDeliveredHandler> logger
) : IOrderDeliveredHandler {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IEscrowClient _escrowClient = escrowClient;
    private readonly ILogger<OrderDeliveredHandler> _logger = logger;

    public async Task<OrderDeliveredOutcome> HandleAsync(
        string orderNumber,
        string driverPrincipalId,
        DateTime deliveredAt
    ) {
        if (string.IsNullOrWhiteSpace(orderNumber)) {
            return new OrderDeliveredOutcome(false, false, "no order number");
        }

        if (string.IsNullOrWhiteSpace(driverPrincipalId)) {
            return new OrderDeliveredOutcome(false, false, "no courier principal");
        }

        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order is null) {
            return new OrderDeliveredOutcome(false, false, "no order carries that number");
        }

        var existing = await _dbContext.ShippingSettlements
            .FirstOrDefaultAsync(s => s.OrderNumber == orderNumber);

        if (existing is not null) {
            return new OrderDeliveredOutcome(true, true, null);
        }

        var now = DateTime.UtcNow;

        var settlement = new ShippingSettlement {
            OrderNumber = orderNumber,
            DriverPrincipalId = driverPrincipalId,
            DeliveredAt = deliveredAt == default ? now : deliveredAt,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _dbContext.ShippingSettlements.Add(settlement);

        if (order.Status is DomainStatus.PAID
            or DomainStatus.PROCESSING_FULFILLMENT
            or DomainStatus.SHIPPED
        ) {
            order.Status = DomainStatus.DELIVERED;
            order.UpdatedAt = now;
        } else if (order.Status is not (DomainStatus.DELIVERED or DomainStatus.COMPLETED)) {
            _logger.LogWarning(
                "{Order} was reported delivered while it stood at {Status}. The fee is recorded as "
              + "owed and the status is left as it is, because a delivery cannot undo a refund.",
                orderNumber, order.Status
            );
        }

        await _dbContext.SaveChangesAsync();

        await TrySettleAsync(settlement);

        return new OrderDeliveredOutcome(true, false, null);
    }

    private async Task TrySettleAsync(ShippingSettlement settlement) {
        try {
            var result = await _escrowClient.SettleShippingFeeAsync(
                settlement.OrderNumber, settlement.DriverPrincipalId
            );

            if (result.Success) {
                settlement.SettledAt = DateTime.UtcNow;
                settlement.NextAttemptAt = null;
                settlement.LastError = null;
            } else {
                settlement.LastError = Trim(result.Detail ?? "payment refused the settlement");
                settlement.NextAttemptAt = DateTime.UtcNow.AddMinutes(1);
            }
        } catch (Exception e) {
            settlement.LastError = Trim(e.Message);
            settlement.NextAttemptAt = DateTime.UtcNow.AddMinutes(1);

            _logger.LogWarning(
                e, "the shipping fee for {Order} is owed and unpaid; it will be retried",
                settlement.OrderNumber
            );
        }

        settlement.Attempts += 1;
        settlement.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    private static string Trim(string message) =>
        message.Length <= 500 ? message : message[..500];
}
