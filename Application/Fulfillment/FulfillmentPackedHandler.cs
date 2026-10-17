using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using DomainStatus = Kinetix.OrderService.Domain.Enums.OrderStatus;
using Kinetix.OrderService.Infrastructure.Persistence;
using Kinetix.OrderService.Application.Lifecycle;

namespace Kinetix.OrderService.Application.Fulfillment;

public class FulfillmentPackedHandler(
    OrderDbContext dbContext,
    ICourierDispatcher dispatcher
) : IFulfillmentPackedHandler {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly ICourierDispatcher _dispatcher = dispatcher;

    public async Task<FulfillmentPackedOutcome> HandleAsync(
        string merchantPrincipalId,
        string orderNumber,
        string fulfillmentTaskId
    ) {
        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order is null) {
            return new FulfillmentPackedOutcome(false, false, string.Empty, "no order carries that number");
        }

        if (!string.Equals(order.MerchantPrincipalId, merchantPrincipalId, StringComparison.Ordinal)) {
            return new FulfillmentPackedOutcome(
                false,
                false,
                string.Empty,
                "that order is not that merchant's",
                "NOT_THIS_MERCHANTS_ORDER"
            );
        }

        if (order.Status is DomainStatus.SHIPPED or DomainStatus.DELIVERED or DomainStatus.COMPLETED) {
            return new FulfillmentPackedOutcome(true, true, string.Empty, null);
        }

        if (order.Status is not (DomainStatus.PAID or DomainStatus.PROCESSING_FULFILLMENT)) {
            return new FulfillmentPackedOutcome(
                false,
                false,
                string.Empty,
                $"the order is {order.Status}, so there is nothing to send",
                "ORDER_NOT_AWAITING_FULFILMENT"
            );
        }

        var now = DateTime.UtcNow;

        if (order.Status is DomainStatus.PAID) {
            _dbContext.ChangeStatus(
                order,
                DomainStatus.PROCESSING_FULFILLMENT,
                LifecycleActor.Merchant(merchantPrincipalId),
                now
            );
        }

        var dispatch = await _dbContext.CourierDispatches.FirstOrDefaultAsync(d => d.OrderNumber == orderNumber);
        if (dispatch is null) {
            dispatch = new CourierDispatch {
                OrderNumber = orderNumber,
                MerchantPrincipalId = merchantPrincipalId,
                FulfillmentTaskId = fulfillmentTaskId,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };

            _dbContext.CourierDispatches.Add(dispatch);
        }

        await _dbContext.SaveChangesAsync();

        if (dispatch.NextAttemptAt is not null) {
            await _dispatcher.AttemptAsync(dispatch, order);
            await _dbContext.SaveChangesAsync();
        }

        return new FulfillmentPackedOutcome(true, false, dispatch.DispatchRef ?? string.Empty, null);
    }
}
