namespace Kinetix.OrderService.DTOs.Responses;

public record ReturnNeedingAttentionResponse(
    string ReturnNumber,
    string OrderNumber,
    string MerchantPrincipalId,
    string Status,
    string Reason,
    DateTime OpenedAt,
    DateTime? GoodsReceivedAt,
    decimal? RefundAmount,
    string? LastRefundError
);
