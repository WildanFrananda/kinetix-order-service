using Common.V1;
using Grpc.Core;
using PaymentProto = global::Payment.V1;

namespace Kinetix.OrderService.Application.Services;

public class EscrowGrpcClient(
    PaymentProto.PaymentService.PaymentServiceClient client,
    ILogger<EscrowGrpcClient> logger
) : IEscrowClient {
    private const decimal MinorPerMajor = 100m;

    private readonly PaymentProto.PaymentService.PaymentServiceClient _client = client;
    private readonly ILogger<EscrowGrpcClient> _logger = logger;

    public async Task<StepResult> CreateHoldAsync(
        string orderNumber,
        string customerPrincipalId,
        string merchantPrincipalId,
        string? driverPrincipalId,
        decimal totalOrderAmount,
        decimal merchantAmount,
        decimal shippingFeeAmount
    ) {

        try {
            await _client.CreateEscrowHoldAsync(new PaymentProto.CreateEscrowHoldRequest {
                OrderNumber = orderNumber,
                CustomerPrincipalId = customerPrincipalId,
                MerchantPrincipalId = merchantPrincipalId,
                DriverPrincipalId = driverPrincipalId ?? string.Empty,
                TotalOrderAmount = ToMoney(totalOrderAmount),
                MerchantAmount = ToMoney(merchantAmount),
                ShippingFeeAmount = ToMoney(shippingFeeAmount),
            });
            return StepResult.Ok();
        } catch (RpcException e) when (
            e.StatusCode == StatusCode.FailedPrecondition
            || e.StatusCode == StatusCode.InvalidArgument
        ) {
            return StepResult.Refused(e.Status.Detail);
        } catch (RpcException e) {
            _logger.LogError(e, "payment did not answer CreateEscrowHold for {Order}", orderNumber);
            throw;
        }
    }

    public async Task<StepResult> RefundHoldAsync(string orderNumber, string reason) {
        var response = await _client.RefundEscrowAsync(new PaymentProto.RefundEscrowRequest {
            OrderNumber = orderNumber,
            Reason = reason,
        });

        return response.Found ? StepResult.Ok() : StepResult.Repeat();
    }

    private static Money ToMoney(decimal amount) => new() {
        AmountMinor = (long)Math.Round(amount * MinorPerMajor, MidpointRounding.AwayFromZero),
        Currency = "IDR",
    };
}
