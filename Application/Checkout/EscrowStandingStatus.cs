namespace Kinetix.OrderService.Application.Checkout;

public enum EscrowStandingStatus {
    Unspecified,
    Held,
    Released,
    Refunded,
    Expired,
    Failed,
}
