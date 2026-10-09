namespace Kinetix.OrderService.Application.Returns;

public enum ReturnRejectionStatus {
    Rejected,
    AlreadyRejected,
    NoSuchReturn,
    GoodsAlreadyReceived,
    NoReasonGiven,
}
