namespace Kinetix.OrderService.DTOs.Responses;

public record OrderSearchResponse(IReadOnlyList<OrderSummaryResponse> Orders, string? NextCursor);
