using Microsoft.EntityFrameworkCore;
using Grpc.Core;
using Common.V1;
using Fleet.V1;
using Kinetix.OrderService.Application.Ports;
using DomainStatus = Kinetix.OrderService.Domain.Enums.OrderStatus;
using Kinetix.OrderService.Infrastructure.Persistence;

namespace Kinetix.OrderService.Application.Fulfillment;

public class FulfillmentPackedHandler(
    OrderDbContext dbContext,
    CourierTelemetryService.CourierTelemetryServiceClient courierClient,
    ILogger<FulfillmentPackedHandler> logger
) : IFulfillmentPackedHandler {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly CourierTelemetryService.CourierTelemetryServiceClient _courierClient = courierClient;
    private readonly ILogger<FulfillmentPackedHandler> _logger = logger;

    public async Task<FulfillmentPackedOutcome> HandleAsync(
        string merchantPrincipalId,
        string orderNumber,
        string fulfillmentTaskId
    ) {
        var order = await _dbContext.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order is null) {
            return new FulfillmentPackedOutcome(false, false, string.Empty, "no order carries that number");
        }

        if (order.Status is DomainStatus.SHIPPED or DomainStatus.DELIVERED or DomainStatus.COMPLETED) {
            return new FulfillmentPackedOutcome(true, true, string.Empty, null);
        }

        order.Status = DomainStatus.PROCESSING_FULFILLMENT;
        order.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var dispatchRef = await DispatchCourier(order, merchantPrincipalId, fulfillmentTaskId);

        return new FulfillmentPackedOutcome(true, false, dispatchRef, null);
    }

    private async Task<string> DispatchCourier(
        Domain.Entities.Order order,
        string merchantPrincipalId,
        string fulfillmentTaskId
    ) {
        try {
            var response = await _courierClient.DispatchCourierAsync(new DispatchCourierRequest {
                MerchantPrincipalId = merchantPrincipalId,
                OrderId = order.Id.ToString(),
                OrderNumber = order.OrderNumber,
                DeliveryAddress = new Address {
                    StreetAddress = order.ShippingAddress,
                    RecipientName = order.RecipientName,
                    PhoneNumber = order.RecipientPhone,
                },
            });

            if (!response.Success) {
                _logger.LogError(
                    "{Order} is packed (warehouse task {Task}) and no courier was assigned: {Reason}",
                    order.OrderNumber, fulfillmentTaskId, response.Error?.Message ?? "no reason given"
                );
                return string.Empty;
            }

            return response.DispatchRef;
        } catch (RpcException e) {
            _logger.LogError(
                e, "{Order} is packed (warehouse task {Task}) but the fleet could not be reached",
                order.OrderNumber, fulfillmentTaskId
            );
            return string.Empty;
        }
    }
}
