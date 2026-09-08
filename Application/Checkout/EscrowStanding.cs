namespace Kinetix.OrderService.Application.Checkout;

public record EscrowStanding(bool Found, EscrowStandingStatus Status, long TotalAmountMinor, string Currency);
