namespace Kinetix.OrderService.Application.Completion;

public enum CompletionStatus {
    Completed,
    AlreadyCompleted,
    NoSuchOrder,
    NotDelivered,
    WindowStillOpen,
    ReturnUnresolved,
}
