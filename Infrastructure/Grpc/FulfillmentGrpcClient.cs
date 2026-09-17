using Common.V1;
using Fulfillment.V1;
using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class FulfillmentGrpcClient(
    FulfillmentService.FulfillmentServiceClient client,
    ILogger<FulfillmentGrpcClient> logger
) : IFulfillmentClient {
    private readonly FulfillmentService.FulfillmentServiceClient _client = client;
    private readonly ILogger<FulfillmentGrpcClient> _logger = logger;

    private const int MinorUnitsPerMajor = 100;

    private static Money ToMoney(decimal major) => new() {
        AmountMinor = (long)decimal.Round(major * MinorUnitsPerMajor, 0, MidpointRounding.AwayFromZero),
        Currency = "IDR",
    };

    public async Task<FulfillmentCreated> CreateOrderAsync(
        string merchantPrincipalId,
        string orderNumber,
        string shippingAddress,
        decimal totalAmount,
        IReadOnlyList<FulfillmentLine> lines
    ) {
        var request = new CreateOrderRequest {
            MerchantPrincipalId = merchantPrincipalId,
            OrderNumber = orderNumber,
            ShippingAddress = new Address { StreetAddress = shippingAddress },
            TotalAmount = ToMoney(totalAmount),
            IdempotencyKey = new IdempotencyKey { Key = orderNumber },
        };
        foreach (var line in lines) {
            request.Items.Add(new OrderItem {
                Sku = line.Sku,
                ProductName = line.ProductName,
                Quantity = line.Quantity,
                UnitPrice = ToMoney(line.UnitPrice),
            });
        }

        try {
            var response = await _client.CreateOrderAsync(request);
            return response.Success
                ? new FulfillmentCreated(StepResult.Ok(), response.OrderId)
                : new FulfillmentCreated(
                    StepResult.Refused(response.Error?.Message ?? "the warehouse refused the order"),
                    string.Empty
                );
        } catch (RpcException e) {
            _logger.LogError(e, "warehouse did not answer CreateOrder for {Order}", orderNumber);
            throw;
        }
    }

    public async Task<StepResult> CancelOrderAsync(string merchantPrincipalId, string warehouseOrderId) {
        if (string.IsNullOrWhiteSpace(warehouseOrderId)) {
            return StepResult.Repeat();
        }

        try {
            var response = await _client.CancelOrderAsync(new CancelOrderRequest {
                MerchantPrincipalId = merchantPrincipalId,
                OrderId = warehouseOrderId,
                Reason = "checkout was rolled back",
                IdempotencyKey = new IdempotencyKey { Key = $"cancel:{warehouseOrderId}" },
            });
            return response.Success
                ? StepResult.Ok()
                : StepResult.Refused(
                    string.IsNullOrWhiteSpace(response.Message)
                        ? "the warehouse refused the cancellation"
                        : response.Message
                  );
        } catch (RpcException e) {
            _logger.LogError(e, "warehouse did not answer CancelOrder for {Id}", warehouseOrderId);
            throw;
        }
    }
}
