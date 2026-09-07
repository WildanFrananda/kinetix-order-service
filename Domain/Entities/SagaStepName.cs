namespace Kinetix.OrderService.Domain.Entities;

public enum SagaStepName {
    RedeemVoucher,
    AllocateFlashSaleStock,
    ReserveStock,
    CreateEscrowHold,
}
