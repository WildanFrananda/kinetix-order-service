using Fulfillment.V1;
using Grpc.Core;

namespace Kinetix.OrderService.Application.Services;

public class StockGrpcClient(
    BinStockService.BinStockServiceClient client,
    ILogger<StockGrpcClient> logger
) : IStockClient {
    private readonly BinStockService.BinStockServiceClient _client = client;
    private readonly ILogger<StockGrpcClient> _logger = logger;

    public async Task<StepResult> ReserveStockAsync(string merchantPrincipalId, string sku, int quantity, string orderNumber) {
        try {
            var response = await _client.ReserveStockAsync(new ReserveStockRequest {
                MerchantPrincipalId = merchantPrincipalId,
                Sku = sku,
                Quantity = quantity,
                OrderNumber = orderNumber,
            });

            return response.Success
                ? StepResult.Ok()
                : StepResult.Refused(response.Error?.Message ?? $"no stock for {sku}");
        } catch (RpcException e) {
            _logger.LogError(e, "warehouse did not answer ReserveStock for {Sku} on {Order}", sku, orderNumber);
            throw;
        }
    }

    public async Task<StepResult> ReleaseStockAsync(string merchantPrincipalId, string sku, int quantity, string orderNumber) {
        var response = await _client.ReleaseStockAsync(new ReleaseStockRequest {
            MerchantPrincipalId = merchantPrincipalId,
            Sku = sku,
            Quantity = quantity,
            OrderNumber = orderNumber,
        });

        if (!response.Success) {
            return StepResult.Refused(response.Error?.Message ?? $"stock for {sku} could not be released");
        }
        return response.AlreadyReleased ? StepResult.Repeat() : StepResult.Ok();
    }
}
