using Grpc.Core;
using Kinetix.OrderService.Infrastructure.Grpc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using PaymentProto = global::Payment.V1;

namespace Kinetix.OrderService.Tests;

public class EscrowGrpcClientTests {
    private const string Customer = "f59fd296-a50f-4a32-970f-a1d1fddd76ae";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static AsyncUnaryCall<PaymentProto.EscrowHoldResponse> Answer(
        PaymentProto.EscrowHoldResponse response) =>
        new(Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { }
        );

    private static EscrowGrpcClient ClientOver(
        Mock<PaymentProto.PaymentService.PaymentServiceClient> payment
    ) => new(payment.Object, NullLogger<EscrowGrpcClient>.Instance);

    [Fact]
    public async Task EveryRefundForOneOrderCarriesTheSameKeyPaymentDedupesOn() {
        var payment = new Mock<PaymentProto.PaymentService.PaymentServiceClient>();
        var keys = new List<string>();

        payment.Setup(c => c.RefundEscrowAsync(
                It.IsAny<PaymentProto.RefundEscrowRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((PaymentProto.RefundEscrowRequest request, Metadata _, DateTime? __, CancellationToken ___) => {
                keys.Add(request.IdempotencyKey.Key);
                return Answer(new PaymentProto.EscrowHoldResponse { Found = true, AlreadyApplied = keys.Count > 1 });
            });

        var client = ClientOver(payment);
        var first = await client.RefundHoldAsync("ORD-KEY-0001", "the checkout failed");
        var second = await client.RefundHoldAsync("ORD-KEY-0001", "retrying after a timeout");

        Assert.Equal(["order:ORD-KEY-0001", "order:ORD-KEY-0001"], keys);

        Assert.True(first.Success);
        Assert.False(first.AlreadyDone);

        Assert.True(second.Success);
        Assert.True(second.AlreadyDone);
    }

    [Fact]
    public async Task ARefundThatFindsNoHoldCarriesAlreadyAppliedRatherThanFlatteningToARefusal() {
        var payment = new Mock<PaymentProto.PaymentService.PaymentServiceClient>();
        payment.SetupSequence(c => c.RefundEscrowAsync(
                It.IsAny<PaymentProto.RefundEscrowRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())
        )
            .Returns(Answer(new PaymentProto.EscrowHoldResponse { Found = false, AlreadyApplied = false }))
            .Returns(Answer(new PaymentProto.EscrowHoldResponse { Found = false, AlreadyApplied = true }));

        var client = ClientOver(payment);

        var tombstoned = await client.RefundHoldAsync("ORD-KEY-0002", "the checkout failed");
        Assert.False(tombstoned.Success);
        Assert.False(tombstoned.AlreadyDone);

        var confirmed = await client.RefundHoldAsync("ORD-KEY-0002", "the checkout failed");
        Assert.False(confirmed.Success);
        Assert.True(confirmed.AlreadyDone);
    }

    [Fact]
    public async Task AHoldCarriesTheSameKeyAndReportsAReplayAsAlreadyDone() {
        var payment = new Mock<PaymentProto.PaymentService.PaymentServiceClient>();
        PaymentProto.CreateEscrowHoldRequest? sent = null;

        payment.Setup(c => c.CreateEscrowHoldAsync(
            It.IsAny<PaymentProto.CreateEscrowHoldRequest>(), It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())
        )
            .Returns((PaymentProto.CreateEscrowHoldRequest request, Metadata _, DateTime? __, CancellationToken ___) => {
                sent = request;
                return Answer(new PaymentProto.EscrowHoldResponse { Found = true, AlreadyApplied = true });
            });

        var result = await ClientOver(payment).CreateHoldAsync(
            "ORD-KEY-0003", Customer, Merchant, null, 1200m, 1000m, 200m);

        Assert.NotNull(sent);
        Assert.Equal("order:ORD-KEY-0003", sent!.IdempotencyKey.Key);
        Assert.Equal(120000, sent.TotalOrderAmount.AmountMinor);
        Assert.Equal("IDR", sent.TotalOrderAmount.Currency);

        Assert.True(result.Success);
        Assert.True(result.AlreadyDone);
    }

    [Fact]
    public async Task ARefusedHoldStaysARefusalRatherThanAnException() {
        var payment = new Mock<PaymentProto.PaymentService.PaymentServiceClient>();
        payment.Setup(c => c.CreateEscrowHoldAsync(
                It.IsAny<PaymentProto.CreateEscrowHoldRequest>(), It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(
                StatusCode.FailedPrecondition, "this customer's wallet has not got that much in it")));

        var result = await ClientOver(payment).CreateHoldAsync(
            "ORD-KEY-0004", Customer, Merchant, null, 1200m, 1000m, 200m);

        Assert.False(result.Success);
        Assert.Contains("wallet", result.Detail);
    }
}
