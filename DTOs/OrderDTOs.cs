namespace Kinetix.OrderService.DTOs;

public record OrderItemResponse(
    Guid Id,
    string ProductId,
    string ProductTitle,
    decimal UnitPrice,
    int Quantity,
    decimal LineSubtotal
);

public record OrderResponse(
    Guid Id,
    string OrderNumber,
    long CustomerId,
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
