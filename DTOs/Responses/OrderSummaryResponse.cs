namespace Kinetix.OrderService.DTOs.Responses;

public record OrderSummaryResponse(
    Guid Id,
    string OrderNumber,
    string Status,
    string CustomerPrincipalId,
    string MerchantPrincipalId,
    decimal FinalTotal,
    decimal FinalShippingFee,
    int ItemCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeliveredAt,
    DateTime? CompletedAt
);
