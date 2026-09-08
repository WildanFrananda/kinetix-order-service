using System.Globalization;
using Common.V1;
using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using PaymentProto = global::Payment.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class EscrowGrpcClient(
    PaymentProto.PaymentService.PaymentServiceClient client,
    ILogger<EscrowGrpcClient> logger
) : IEscrowClient {
    private const string IdempotencyKeyPrefix = "order:";

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
        var inexact = new (string Field, decimal Amount)[] {
            ("total", totalOrderAmount), ("merchant", merchantAmount), ("shipping", shippingFeeAmount),
        }.Where(a => !MinorUnits.IsWhole(a.Amount)).ToList();

        if (inexact.Count > 0) {
            var detail = string.Join(", ", inexact.Select(
                a => $"{a.Field} {a.Amount.ToString(CultureInfo.InvariantCulture)}"
            ));

            _logger.LogError(
                "refusing the escrow hold for {Order}: {Detail} cannot be expressed in whole minor "
              + "units, so the amount debited would differ from the amount on the order row",
                orderNumber, detail
            );

            return StepResult.Refused(
                $"an amount on this order cannot be charged exactly ({detail}); nothing was debited"
            );
        }

        try {
            var response = await _client.CreateEscrowHoldAsync(new PaymentProto.CreateEscrowHoldRequest {
                OrderNumber = orderNumber,
                CustomerPrincipalId = customerPrincipalId,
                MerchantPrincipalId = merchantPrincipalId,
                DriverPrincipalId = driverPrincipalId ?? string.Empty,
                TotalOrderAmount = ToMoney(totalOrderAmount),
                MerchantAmount = ToMoney(merchantAmount),
                ShippingFeeAmount = ToMoney(shippingFeeAmount),
                IdempotencyKey = KeyFor(orderNumber),
            });

            return response.AlreadyApplied ? StepResult.Repeat() : StepResult.Ok();
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
            IdempotencyKey = KeyFor(orderNumber),
        });

        if (response.Found) {
            return response.AlreadyApplied ? StepResult.Repeat() : StepResult.Ok();
        }

        _logger.LogWarning(
            "payment reported no escrow hold to refund for {Order} (reason: {Reason}, "
                + "already_applied: {AlreadyApplied})",
            orderNumber, reason, response.AlreadyApplied
        );

        return StepResult.Absent(
            response.AlreadyApplied, "payment held nothing to refund for this order"
        );
    }

    public async Task<EscrowStanding> GetStandingAsync(string orderNumber) {
        var response = await _client.GetEscrowStatusAsync(new PaymentProto.GetEscrowStatusRequest {
            OrderNumber = orderNumber,
        });

        return new EscrowStanding(
            response.Found,
            Map(response.Status),
            response.TotalOrderAmount?.AmountMinor ?? 0,
            response.TotalOrderAmount?.Currency ?? string.Empty
        );
    }

    private static IdempotencyKey KeyFor(string orderNumber) =>
        new() { Key = IdempotencyKeyPrefix + orderNumber };

    private static EscrowStandingStatus Map(PaymentProto.EscrowStatus status) => status switch {
        PaymentProto.EscrowStatus.Held => EscrowStandingStatus.Held,
        PaymentProto.EscrowStatus.Released => EscrowStandingStatus.Released,
        PaymentProto.EscrowStatus.Refunded => EscrowStandingStatus.Refunded,
        PaymentProto.EscrowStatus.Expired => EscrowStandingStatus.Expired,
        PaymentProto.EscrowStatus.Failed => EscrowStandingStatus.Failed,
        _ => EscrowStandingStatus.Unspecified,
    };

    private static Money ToMoney(decimal amount) => new() {
        AmountMinor = decimal.ToInt64(amount * MinorUnits.PerMajor),
        Currency = "IDR",
    };
}
