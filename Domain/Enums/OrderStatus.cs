namespace Kinetix.OrderService.Domain.Enums;

public enum OrderStatus {
    PENDING_PAYMENT = 0,
    PAID = 1,
    PROCESSING_FULFILLMENT = 2,
    SHIPPED = 3,
    DELIVERED = 4,
    COMPLETED = 5,
    CANCELLED = 6,
    REFUNDED = 7
}
