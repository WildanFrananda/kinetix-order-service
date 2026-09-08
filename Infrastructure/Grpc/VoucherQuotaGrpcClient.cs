using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using Pricing.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class VoucherQuotaGrpcClient(
    PricingService.PricingServiceClient client,
    ILogger<VoucherQuotaGrpcClient> logger
) : IVoucherQuotaClient {
    private readonly PricingService.PricingServiceClient _client = client;
    private readonly ILogger<VoucherQuotaGrpcClient> _logger = logger;

    public async Task<StepResult> RedeemVoucherAsync(string voucherCode, string orderNumber, string customerPrincipalId) {
        try {
            var response = await _client.RedeemVoucherAsync(new RedeemVoucherRequest {
                VoucherCode = voucherCode,
                OrderNumber = orderNumber,
                CustomerPrincipalId = customerPrincipalId,
            });

            if (!response.Success) {
                return StepResult.Refused(response.Error?.Message ?? "the voucher was refused");
            }
            return response.AlreadyRedeemed ? StepResult.Repeat() : StepResult.Ok();
        } catch (RpcException e) {
            _logger.LogError(e, "pricing did not answer RedeemVoucher for {Order}", orderNumber);
            throw;
        }
    }

    public async Task<StepResult> ReleaseVoucherAsync(string voucherCode, string orderNumber) {
        var response = await _client.ReleaseVoucherRedemptionAsync(new ReleaseVoucherRedemptionRequest {
            VoucherCode = voucherCode,
            OrderNumber = orderNumber,
        });

        if (!response.Success) {
            return StepResult.Refused(response.Error?.Message ?? "the redemption could not be released");
        }
        return response.AlreadyReleased ? StepResult.Repeat() : StepResult.Ok();
    }
}
