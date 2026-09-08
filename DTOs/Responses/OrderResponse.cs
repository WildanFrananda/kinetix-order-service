namespace Kinetix.OrderService.DTOs.Responses;

public record OrderResponse(
    Guid Id,
    string OrderNumber,
    string CustomerPrincipalId,
    string Status,
    decimal Subtotal,
    decimal DiscountAmount,
    string? AppliedVoucher,
    decimal FinalTotal,
    string ShippingAddress,
    string ShippingServiceTier,
    decimal BaseShippingFee,
    decimal ShippingDiscount,
    decimal FinalShippingFee,
    double DistanceKm,
    DateTime CreatedAt,
    List<OrderItemResponse> Items
);
