using Grpc.Core;
using Pricing.V1;

namespace Kinetix.OrderService.Application.Services;

public class FlashSaleGrpcClient(
    PricingService.PricingServiceClient client,
    ILogger<FlashSaleGrpcClient> logger
) : IFlashSaleClient {
    private readonly PricingService.PricingServiceClient _client = client;
    private readonly ILogger<FlashSaleGrpcClient> _logger = logger;

    public async Task<StepResult> AllocateAsync(string flashSaleId, string productId, int quantity, string orderNumber) {
        try {
            var response = await _client.AllocateFlashSaleStockAsync(new AllocateFlashSaleStockRequest {
                FlashSaleId = flashSaleId,
                ProductId = productId,
                Quantity = quantity,
                OrderNumber = orderNumber,
            });

            if (!response.Success) {
                return StepResult.Refused(response.Error?.Message ?? "the flash sale has no stock left");
            }
            return response.AlreadyAllocated ? StepResult.Repeat() : StepResult.Ok();
        } catch (RpcException e) {
            _logger.LogError(e, "pricing did not answer AllocateFlashSaleStock for {Order}", orderNumber);
            throw;
        }
    }

    public async Task<StepResult> ReleaseAsync(string flashSaleId, string productId, int quantity, string orderNumber) {
        var response = await _client.ReleaseFlashSaleAllocationAsync(new ReleaseFlashSaleAllocationRequest {
            FlashSaleId = flashSaleId,
            ProductId = productId,
            Quantity = quantity,
            OrderNumber = orderNumber,
        });

        if (!response.Success) {
            return StepResult.Refused(response.Error?.Message ?? "the allocation could not be released");
        }
        return response.AlreadyReleased ? StepResult.Repeat() : StepResult.Ok();
    }
}
