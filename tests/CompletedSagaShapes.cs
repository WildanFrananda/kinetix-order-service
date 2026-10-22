using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Tests;

public static class CompletedSagaShapes {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    public static List<CheckoutSagaStep> Current(
        Guid sagaId,
        string orderNumber,
        bool voucher = false,
        bool flashSale = false
    ) {
        var steps = new List<CheckoutSagaStep>();

        if (voucher) {
            steps.Add(Done(sagaId, SagaStepName.RedeemVoucher, "DISCOUNT10"));
        }

        if (flashSale) {
            steps.Add(Done(sagaId, SagaStepName.AllocateFlashSaleStock, "7d2c1b90-4e35-4a1f-9c88-15b0e6a4f302"));
        }

        steps.Add(Done(sagaId, SagaStepName.ReserveStock, "SKU-1"));
        steps.Add(Done(sagaId, SagaStepName.ReserveStock, "SKU-2"));
        steps.Add(Done(sagaId, SagaStepName.CreateEscrowHold, orderNumber));
        steps.Add(Done(sagaId, SagaStepName.CreateFulfillmentOrder, "901"));

        return steps;
    }

    public static List<CheckoutSagaStep> BeforeFulfillment(Guid sagaId, string orderNumber) => [
        Done(sagaId, SagaStepName.ReserveStock, "SKU-1"),
        Done(sagaId, SagaStepName.CreateEscrowHold, orderNumber),
    ];

    private static CheckoutSagaStep Done(Guid sagaId, SagaStepName name, string reference) => new() {
        SagaId = sagaId,
        Name = name,
        Reference = reference,
        Quantity = 1,
        MerchantPrincipalId = Merchant,
        State = SagaStepState.Done,
    };
}
