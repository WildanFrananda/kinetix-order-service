namespace Kinetix.OrderService.Application.Services;

public record EscrowStanding(bool Found, EscrowStandingStatus Status, long TotalAmountMinor, string Currency);
