namespace Kinetix.OrderService.DTOs.Responses;

public record OrderCompletionResponse(string OrderNumber, bool EscrowReleased, string? Detail);
