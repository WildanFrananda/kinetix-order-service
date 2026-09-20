namespace Kinetix.OrderService.Application.Delivery;

public record OrderDeliveredOutcome(bool Accepted, bool AlreadyDelivered, string? Error);
