using Grpc.Core;
using Common.V1;
using Fulfillment.V1;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class FulfillmentGrpcClient(
    FulfillmentTaskService.FulfillmentTaskServiceClient client,
    ILogger<FulfillmentGrpcClient> logger
) : IFulfillmentClient {
    private readonly FulfillmentTaskService.FulfillmentTaskServiceClient _client = client;
    private readonly ILogger<FulfillmentGrpcClient> _logger = logger;

    public async Task<FulfillmentCreated> CreateTaskAsync(
        string merchantPrincipalId,
        string orderNumber,
        IReadOnlyList<FulfillmentLine> lines
    ) {
        var request = new CreateFulfillmentTaskRequest {
            MerchantPrincipalId = merchantPrincipalId,
            OrderNumber = orderNumber,
            IdempotencyKey = new IdempotencyKey { Key = orderNumber },
        };
        foreach (var line in lines) {
            request.Lines.Add(new FulfillmentTaskLine {
                Sku = line.Sku,
                Quantity = line.Quantity,
            });
        }

        try {
            var response = await _client.CreateFulfillmentTaskAsync(request);
            if (!response.Success) {
                return new FulfillmentCreated(
                    StepResult.Refused(response.Error?.Message ?? "the warehouse refused the work"),
                    string.Empty
                );
            }

            if (response.AlreadyCreated) {
                _logger.LogInformation(
                    "warehouse already had a task for {Order}; reusing {Task}",
                    orderNumber, response.TaskId
                );
            }

            return new FulfillmentCreated(StepResult.Ok(), response.TaskId);
        } catch (RpcException e) {
            _logger.LogError(e, "warehouse did not answer CreateFulfillmentTask for {Order}", orderNumber);
            throw;
        }
    }

    public async Task<StepResult> CancelTaskAsync(string merchantPrincipalId, string fulfillmentTaskId) {
        if (string.IsNullOrWhiteSpace(fulfillmentTaskId)) {
            return StepResult.Repeat();
        }

        try {
            var response = await _client.CancelFulfillmentTaskAsync(new CancelFulfillmentTaskRequest {
                MerchantPrincipalId = merchantPrincipalId,
                TaskId = fulfillmentTaskId,
                Reason = "checkout was rolled back",
            });

            if (response.Success) {
                return response.AlreadyCancelled ? StepResult.Repeat() : StepResult.Ok();
            }

            return StepResult.Refused(response.Error?.Message ?? "the warehouse refused the cancellation");
        } catch (RpcException e) {
            _logger.LogError(e, "warehouse did not answer CancelFulfillmentTask for {Task}", fulfillmentTaskId);
            throw;
        }
    }
}
